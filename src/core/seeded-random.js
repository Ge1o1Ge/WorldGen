function hashStream(seed, streamName) {
  let hash = seed >>> 0;
  for (let index = 0; index < streamName.length; index += 1) {
    hash ^= streamName.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash || 0x6d2b79f5;
}

export class SeededRandom {
  constructor(seed, streamName) {
    this.state = hashStream(seed, streamName);
  }

  next() {
    this.state = (this.state + 0x6d2b79f5) >>> 0;
    let value = this.state;
    value = Math.imul(value ^ (value >>> 15), value | 1);
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  }
}

export function createRandomStreams(seed, names) {
  return Object.fromEntries(
    [...names].sort().map((name) => [name, new SeededRandom(seed, name)])
  );
}
