export class BoundedSemaphore {
  private active = 0;
  private readonly waiters: Array<{
    readonly resolve: (release: () => void) => void;
    readonly reject: (error: Error) => void;
    readonly signal: AbortSignal;
    readonly onAbort: () => void;
  }> = [];

  public constructor(private readonly capacity: number) {}

  public async acquire(signal: AbortSignal): Promise<() => void> {
    if (signal.aborted) throw abortError();
    if (this.active < this.capacity) {
      this.active += 1;
      return this.releaseOnce();
    }

    return new Promise<() => void>((resolve, reject) => {
      const waiter = {
        resolve,
        reject,
        signal,
        onAbort: () => {
          const index = this.waiters.indexOf(waiter);
          if (index >= 0) this.waiters.splice(index, 1);
          reject(abortError());
        },
      };
      signal.addEventListener('abort', waiter.onAbort, { once: true });
      this.waiters.push(waiter);
    });
  }

  private releaseOnce(): () => void {
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.release();
    };
  }

  private release(): void {
    while (this.waiters.length > 0) {
      const waiter = this.waiters.shift();
      if (!waiter) break;
      waiter.signal.removeEventListener('abort', waiter.onAbort);
      if (waiter.signal.aborted) {
        waiter.reject(abortError());
        continue;
      }
      waiter.resolve(this.releaseOnce());
      return;
    }
    this.active -= 1;
  }
}

export function abortError(): Error {
  const error = new Error('Operation cancelled.');
  error.name = 'AbortError';
  return error;
}
