// One bounded cache per viewer; only desired chunks are queued, at most four requests at once.
export class SphereChunkCache {
  constructor({ fetchChunk, onChange = () => {}, capacity = 192, concurrency = 4 }) {
    this.fetchChunk = fetchChunk;
    this.onChange = onChange;
    this.capacity = capacity;
    this.concurrency = concurrency;
    this.chunks = new Map();
    this.pending = new Map();
    this.failed = new Set();
    this.desired = new Set();
    this.generation = 0;
  }

  get(key) { return this.chunks.get(key); }

  setDesired(keys) {
    this.desired = new Set(keys);
    for (const key of this.failed) if (!this.desired.has(key)) this.failed.delete(key);
    for (const key of this.desired) {
      const chunk = this.chunks.get(key);
      if (chunk) { this.chunks.delete(key); this.chunks.set(key, chunk); }
    }
    this.trim();
    this.pump();
  }

  trim() {
    for (const key of this.chunks.keys()) {
      if (this.chunks.size <= this.capacity) break;
      if (!this.desired.has(key)) this.chunks.delete(key);
    }
  }

  invalidate() {
    this.generation++;
    this.chunks.clear();
    this.failed.clear();
    // Keep pending slots until aborted requests settle; the concurrency limit remains true.
    for (const controller of this.pending.values()) controller.abort();
  }

  retry() { this.failed.clear(); this.pump(); }

  get status() {
    let loaded = 0;
    let failed = 0;
    for (const key of this.desired) {
      if (this.chunks.has(key)) loaded++;
      if (this.failed.has(key)) failed++;
    }
    return { loaded, total: this.desired.size, failed, resident: this.chunks.size };
  }

  pump() {
    for (const key of this.desired) {
      if (this.pending.size >= this.concurrency) break;
      if (this.chunks.has(key) || this.pending.has(key) || this.failed.has(key)) continue;
      // Never cycle through an oversized working set, evicting chunks still in view.
      if (this.chunks.size + this.pending.size >= this.capacity &&
          ![...this.chunks.keys()].some(item => !this.desired.has(item))) break;
      const generation = this.generation;
      const controller = new AbortController();
      this.pending.set(key, controller);
      Promise.resolve().then(() => this.fetchChunk(key, controller.signal)).then(chunk => {
        if (generation !== this.generation) return;
        if (this.chunks.size >= this.capacity) {
          const disposable = [...this.chunks.keys()].find(item => !this.desired.has(item));
          if (disposable !== undefined) this.chunks.delete(disposable);
        }
        if (this.chunks.size < this.capacity) this.chunks.set(key, chunk);
      }).catch(error => {
        if (generation === this.generation && error.name !== "AbortError") this.failed.add(key);
      }).finally(() => {
        this.pending.delete(key);
        this.trim();
        this.pump();
        this.onChange();
      });
    }
  }
}
