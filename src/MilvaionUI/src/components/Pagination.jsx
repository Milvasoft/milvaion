import Icon from './Icon'
import './Pagination.css'

/* eslint-disable react/prop-types */

/**
 * Page navigation for a server-paged list.
 *
 * This markup was written inline in JobList and copied into several other pages, and its
 * styling exists in ten separate stylesheets under the same class names - so which rules
 * actually applied depended on bundle order. Everything here is prefixed and self
 * contained, so a page using this component does not depend on any of that.
 *
 * Renders nothing when there is a single page: navigation for one page is noise.
 *
 * @param {number} page Current page, 1 based.
 * @param {number} pageSize Rows per page.
 * @param {number} totalCount Total rows across all pages.
 * @param {(page: number) => void} onPageChange
 * @param {(size: number) => void} onPageSizeChange Omit to hide the size selector.
 * @param {number[]} pageSizeOptions
 */
function Pagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
  onPageSizeChange = null,
  pageSizeOptions = [10, 20, 50, 100],
}) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))

  if (totalPages <= 1 && !onPageSizeChange) return null

  // A window of page numbers around the current one, so a list with hundreds of pages
  // does not render hundreds of buttons.
  const maxVisible = 5
  let startPage = Math.max(1, page - Math.floor(maxVisible / 2))
  const endPage = Math.min(totalPages, startPage + maxVisible - 1)

  if (endPage - startPage + 1 < maxVisible) {
    startPage = Math.max(1, endPage - maxVisible + 1)
  }

  const pages = Array.from({ length: endPage - startPage + 1 }, (_, i) => startPage + i)

  const go = (target) => {
    const clamped = Math.min(Math.max(1, target), totalPages)

    if (clamped !== page) onPageChange(clamped)
  }

  return (
    <div className="mv-pagination">
      {totalPages > 1 && (
        <div className="mv-pagination-pages">
          <button className="mv-page-btn" onClick={() => go(1)} disabled={page === 1} title="First page">
            <Icon name="first_page" size={18} />
          </button>
          <button className="mv-page-btn" onClick={() => go(page - 1)} disabled={page === 1} title="Previous page">
            <Icon name="chevron_left" size={18} />
          </button>

          {startPage > 1 && <span className="mv-page-ellipsis">…</span>}

          {pages.map(p => (
            <button
              key={p}
              className={'mv-page-btn' + (p === page ? ' is-current' : '')}
              onClick={() => go(p)}
              aria-current={p === page ? 'page' : undefined}
            >
              {p}
            </button>
          ))}

          {endPage < totalPages && <span className="mv-page-ellipsis">…</span>}

          <button className="mv-page-btn" onClick={() => go(page + 1)} disabled={page === totalPages} title="Next page">
            <Icon name="chevron_right" size={18} />
          </button>
          <button className="mv-page-btn" onClick={() => go(totalPages)} disabled={page === totalPages} title="Last page">
            <Icon name="last_page" size={18} />
          </button>

          <span className="mv-page-info">
            Page {page} of {totalPages} ({totalCount} total)
          </span>
        </div>
      )}

      {onPageSizeChange && (
        <div className="mv-page-size">
          <label htmlFor="mv-page-size-select">Rows per page</label>
          <select
            id="mv-page-size-select"
            value={pageSize}
            onChange={(e) => onPageSizeChange(Number(e.target.value))}
          >
            {pageSizeOptions.map(size => (
              <option key={size} value={size}>{size}</option>
            ))}
          </select>
        </div>
      )}
    </div>
  )
}

export default Pagination
