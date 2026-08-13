export class DeferredAppResourceDisposal {
  private timer: ReturnType<typeof setTimeout> | null = null

  cancel() {
    if (this.timer === null) return
    clearTimeout(this.timer)
    this.timer = null
  }

  schedule(dispose: () => void) {
    this.cancel()
    this.timer = setTimeout(() => {
      this.timer = null
      dispose()
    }, 0)
  }
}
