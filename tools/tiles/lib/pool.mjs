// Runs worker over items with at most `limit` in flight; rejects on the first failure.
export async function pool(items, limit, worker) {
  const iterator = items[Symbol.iterator]();
  let failed = null;
  async function lane() {
    for (;;) {
      if (failed) return;
      const next = iterator.next();
      if (next.done) return;
      try {
        await worker(next.value);
      } catch (error) {
        failed ??= error;
      }
    }
  }
  await Promise.all(Array.from({ length: limit }, lane));
  if (failed) throw failed;
}
