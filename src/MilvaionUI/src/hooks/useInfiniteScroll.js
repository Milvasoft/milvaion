import { useEffect, useRef } from 'react'

/**
 * Calls `onLoadMore` when a sentinel element scrolls into view.
 *
 * The observer logic was written inline in JobList and was about to be copied into the
 * workflow list and the job picker. Three copies of the same effect drift: one gets a
 * `rootMargin` tweak, another forgets to guard against firing while a load is in flight.
 *
 * Works for both page scrolling and scrolling inside a container - pass `getRoot` for the
 * latter, which is what a dropdown needs.
 *
 * @param {object} options
 * @param {boolean} options.hasMore Whether another page exists.
 * @param {boolean} options.loading Whether a load is already running.
 * @param {() => void} options.onLoadMore
 * @param {boolean} [options.enabled] Set false to detach, e.g. when the list is hidden.
 * @param {() => Element|null} [options.getRoot] Scroll container. Defaults to the viewport.
 * @param {string} [options.rootMargin] How early to trigger.
 * @returns {React.RefObject} Ref to place on the sentinel element.
 */
export function useInfiniteScroll({
  hasMore,
  loading,
  onLoadMore,
  enabled = true,
  getRoot = null,
  rootMargin = '200px',
}) {
  const sentinelRef = useRef(null)

  // Both are read through refs so a caller passing inline arrows - which is the natural
  // way to write them - does not tear down and rebuild the observer on every render.
  const onLoadMoreRef = useRef(onLoadMore)
  onLoadMoreRef.current = onLoadMore

  const getRootRef = useRef(getRoot)
  getRootRef.current = getRoot

  useEffect(() => {
    if (!enabled || !hasMore) return

    const sentinel = sentinelRef.current

    if (!sentinel) return

    const observer = new IntersectionObserver(
      (entries) => {
        // `loading` is captured per effect run, and the effect re-runs when it changes,
        // so this stays accurate without adding the callback to the dependencies.
        if (entries[0].isIntersecting && hasMore && !loading) {
          onLoadMoreRef.current()
        }
      },
      { root: getRootRef.current ? getRootRef.current() : null, rootMargin, threshold: 0 }
    )

    observer.observe(sentinel)

    return () => observer.disconnect()
  }, [enabled, hasMore, loading, rootMargin])

  return sentinelRef
}

export default useInfiniteScroll
