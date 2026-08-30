export function mergeLiveSimulationState(current, patch) {
  if (!current) return patch;
  const previousCities = new Map((current.cities ?? []).map(city => [city.id, city]));
  const cities = (patch.cities ?? current.cities ?? []).map(city => {
    const previous = previousCities.get(city.id) ?? {};
    const previousHomes = new Map((previous.homes ?? []).map(home => [home.id, home]));
    return {
      ...previous,
      ...city,
      stocks: {...(previous.stocks ?? {}), ...(city.stocks ?? {})},
      technology: {...(previous.technology ?? {}), ...(city.technology ?? {})},
      settlement: {...(previous.settlement ?? {}), ...(city.settlement ?? {})},
      homes: city.homes
        ? city.homes.map(home => ({...(previousHomes.get(home.id) ?? {}), ...home}))
        : previous.homes
    };
  });
  const events = [...(patch.events ?? []), ...(current.events ?? [])]
    .filter((event, index, all) => index === all.findIndex(peer =>
      (event.id && peer.id === event.id) || (!event.id && peer.day === event.day && peer.type === event.type && peer.subjectId === event.subjectId)))
    .sort((a, b) => b.day - a.day).slice(0, 240);
  return {
    ...current,
    ...patch,
    map: patch.map ? {...(current.map ?? {}), ...patch.map} : current.map,
    weatherMap: patch.weatherMap ?? current.weatherMap,
    atmosphere: patch.atmosphere ?? current.atmosphere,
    cities,
    events,
    _liveMapChanged: Boolean(patch.map)
  };
}

export class SimulationLiveChannel {
  constructor({url, socketFactory=value => new WebSocket(value), onMessage=()=>{}, onStatus=()=>{}, onError=()=>{}}) {
    Object.assign(this, {url, socketFactory, onMessage, onStatus, onError});
    this.socket = null;
    this.ready = false;
    this.playing = false;
    this.speed = 1;
    this.closed = false;
  }
  connect() {
    if (this.socket || this.closed) return;
    const socket = this.socket = this.socketFactory(this.url);
    socket.addEventListener("open", () => { this.ready = true; this.onStatus("ready"); });
    socket.addEventListener("message", event => {
      try {
        const message = JSON.parse(event.data);
        if (message.type === "busy") this.playing = false;
        if (message.type === "paused") this.playing = false;
        this.onMessage(message, typeof event.data === "string" ? event.data.length : 0);
      } catch (error) { this.onError(error); }
    });
    socket.addEventListener("error", () => this.onError(new Error("WebSocket недоступен")));
    socket.addEventListener("close", () => {
      this.ready = false; this.playing = false; this.socket = null;
      if (!this.closed) this.onStatus("closed");
    });
  }
  send(value) {
    if (!this.ready || this.socket?.readyState !== 1) return false;
    this.socket.send(JSON.stringify(value)); return true;
  }
  setSpeed(speed) {
    if (![1, 7, 30].includes(speed)) throw new RangeError("speed must be 1, 7 or 30");
    this.speed = speed;
    if (this.playing) this.send({type: "run", speed});
  }
  start() { if (this.send({type: "run", speed: this.speed})) { this.playing = true; this.onStatus("playing"); } }
  pause() { if (this.send({type: "pause"})) { this.playing = false; this.onStatus("pausing"); } }
  acknowledge() { return this.send({type: "ack"}); }
  toggle() { if (this.playing) this.pause(); else this.start(); }
  close() {
    this.closed = true; this.playing = false;
    try { this.send({type: "pause"}); this.socket?.close(1000, "page hidden"); } catch {}
    this.socket = null; this.ready = false;
  }
}
