import { useState, useMemo, useCallback } from 'react'
import Icon from './Icon'
import './JsonView.css'

/* eslint-disable react/prop-types */

/**
 * The application's JSON surface, reading and writing.
 *
 * There used to be three implementations. `JsonViewer` highlighted with regexes and
 * `dangerouslySetInnerHTML`; the failed-execution page printed a raw `<pre>` around an
 * unguarded `JSON.parse`; `JsonEditor` was a textarea with beautify, minify and a validity
 * badge. Each had something the others lacked, so which features you got depended on which
 * screen you happened to be on - the execution result had no search, the job payload had no
 * tree, and only the form told you whether the document was valid.
 *
 * This is all of it in one place. `JsonView` reads; `JsonEditor` is the same component with
 * `editable` set, so the editing screens get the tree, the search and the copy affordances
 * that until now only existed in read-only views, and the read-only screens get the
 * validity reporting that only existed in the form.
 *
 * ── Read ──
 *
 * A tree rendered from the parsed value, with collapsing per node, search across keys and
 * values, copy of the whole document, one value, or the path to a field, and a raw text
 * view.
 *
 * This replaces two things. The failed-execution page printed its payload with
 *
 *     <pre>{JSON.stringify(JSON.parse(job.jobData || '{}'), null, 2)}</pre>
 *
 * which has a bug worth stating plainly: `JSON.parse` is unguarded and runs during render,
 * so malformed data throws and takes the whole page down with it. That page lists failed
 * executions, and "invalid job data" is one of the failure types the system records - so
 * the one case where you most need to look at the payload was the case that showed you a
 * blank screen instead. Nothing here can throw; anything that will not parse is shown as
 * text with a note saying so.
 *
 * The other was `JsonViewer`, which highlighted by running regexes over a string and
 * injecting markup through `dangerouslySetInnerHTML`. Beyond the obvious fragility, the
 * number pattern ran across the whole document including the spans it had just inserted,
 * so digits inside a string value - `"listening on 8080"` - came out coloured as numbers.
 * This renders real elements from the parsed value, so a string is a string.
 *
 * What it adds, all of it aimed at reading someone else's payload during an incident:
 *
 *   - collapsing per node, with the child count on the collapsed summary, so a large
 *     object can be skimmed at one level before being opened
 *   - search that matches keys and values, auto-opens the branches containing a hit, and
 *     says how many there are
 *   - copy the whole document, or one value, or the path to a field - the last is what
 *     gets pasted into a bug report
 *   - a raw view, because sometimes the exact bytes are the question
 *   - long arrays rendered in pages rather than all at once
 *
 * ── Write ──
 *
 * With `editable`, a third view appears: a textarea, with live validation, beautify and
 * minify. The tree stays available beside it, which the old editor could not offer - a
 * hand-written payload is exactly the kind that benefits from being read back as a
 * structure before it is saved.
 *
 * @param {string|object} data JSON text or an already-parsed value.
 * @param {string} name Label for the root node.
 * @param {number} maxHeight Pixels before the body scrolls on its own.
 * @param {boolean} editable Offer the edit view.
 * @param {(event: {target: {name: string, value: string}}) => void} onChange
 *   Called on every keystroke, beautify and minify. Shaped like a DOM change event because
 *   the forms that consume it feed a single `handleChange` from every field.
 * @param {string} inputName `name` reported on that synthetic event.
 * @param {number} rows Height of the textarea, in rows.
 * @param {string} placeholder Shown when the textarea is empty.
 * @param {boolean} collapsible Start as a one-line summary that opens on click.
 */

/** How many children of one array or object are rendered before "show more". */
const _pageSize = 100

/** Depth to which the tree is open on first render. */
const _initialDepth = 2

/* ── Parsing ─────────────────────────────────────────────────────────────── */

/**
 * Never throws. Returns what was understood, plus the raw text either way, so the raw
 * view works whether or not the parse succeeded.
 */
function parseSafely(data) {
  if (data == null) return { ok: false, value: null, raw: '', empty: true }

  if (typeof data !== 'string') {
    try {
      return { ok: true, value: data, raw: JSON.stringify(data, null, 2) }
    } catch {
      // Circular references reach here. Rare from an API, not impossible from a caller.
      return { ok: false, value: null, raw: String(data), error: 'Value could not be serialised.' }
    }
  }

  const text = data.trim()

  if (text === '') return { ok: false, value: null, raw: '', empty: true }

  try {
    const value = JSON.parse(text)

    return { ok: true, value, raw: JSON.stringify(value, null, 2) }
  } catch (err) {
    return { ok: false, value: null, raw: data, error: err.message }
  }
}

/* ── Shape helpers ───────────────────────────────────────────────────────── */

const isContainer = (value) => value !== null && typeof value === 'object'

const childrenOf = (value) =>
  Array.isArray(value)
    ? value.map((item, index) => [index, item])
    : Object.entries(value)

/** The one-line stand-in for a collapsed node. */
function summarise(value) {
  if (Array.isArray(value)) {
    return value.length === 0 ? '[]' : `[ ${value.length} ${value.length === 1 ? 'item' : 'items'} ]`
  }

  const keys = Object.keys(value)

  return keys.length === 0 ? '{}' : `{ ${keys.length} ${keys.length === 1 ? 'key' : 'keys'} }`
}

/**
 * Does this subtree contain the term, in a key or a value?
 *
 * Depth-limited rather than unbounded: a pathological payload should slow the search down,
 * not hang the tab.
 */
function subtreeMatches(value, term, depth = 0) {
  if (depth > 12) return false

  if (!isContainer(value)) {
    return String(value).toLowerCase().includes(term)
  }

  return childrenOf(value).some(([key, child]) =>
    String(key).toLowerCase().includes(term) || subtreeMatches(child, term, depth + 1)
  )
}

/** Marks the matching run inside a piece of text so it can be drawn as a highlight. */
function splitOnTerm(text, term) {
  if (!term) return [text]

  const lower = String(text).toLowerCase()
  const at = lower.indexOf(term)

  if (at === -1) return [String(text)]

  const source = String(text)

  return [source.slice(0, at), source.slice(at, at + term.length), source.slice(at + term.length)]
}

function Highlight({ text, term }) {
  const parts = splitOnTerm(text, term)

  if (parts.length === 1) return parts[0]

  return (
    <>
      {parts[0]}
      <mark className="mvj-hit">{parts[1]}</mark>
      {parts[2]}
    </>
  )
}

/* ── Scalar ──────────────────────────────────────────────────────────────── */

function Scalar({ value, term }) {
  if (value === null) return <span className="mvj-null">null</span>
  if (typeof value === 'boolean') return <span className="mvj-bool">{String(value)}</span>
  if (typeof value === 'number') return <span className="mvj-number">{String(value)}</span>

  return (
    <span className="mvj-string">
      &quot;<Highlight text={String(value)} term={term} />&quot;
    </span>
  )
}

/* ── Node ────────────────────────────────────────────────────────────────── */

function Node({ name, value, path, depth, term, openPaths, toggle, onCopy }) {
  const [shown, setShown] = useState(_pageSize)

  const container = isContainer(value)

  /*
   * While searching, a branch holding a hit is opened regardless of what the user had
   * collapsed - and their collapsed state is not overwritten, so clearing the search
   * puts the tree back the way they left it.
   */
  const searchOpen = term ? subtreeMatches(value, term) : false
  const open = term ? searchOpen : openPaths.has(path) || depth < _initialDepth

  const entries = container ? childrenOf(value) : []
  const visible = entries.slice(0, shown)
  const hidden = entries.length - visible.length

  return (
    <div className="mvj-node" style={{ '--depth': depth }}>
      <div className="mvj-line">
        {container ? (
          <button
            type="button"
            className="mvj-twisty"
            onClick={() => toggle(path)}
            aria-expanded={open}
            aria-label={open ? 'Collapse' : 'Expand'}
            disabled={!!term}
          >
            <Icon name={open ? 'expand_more' : 'chevron_right'} size={15} />
          </button>
        ) : (
          <span className="mvj-twisty is-empty" />
        )}

        {name !== undefined && (
          <>
            <span className="mvj-key">
              <Highlight text={String(name)} term={term} />
            </span>
            <span className="mvj-colon">:</span>
          </>
        )}

        {container
          ? <span className="mvj-summary">{open ? (Array.isArray(value) ? '[' : '{') : summarise(value)}</span>
          : <Scalar value={value} term={term} />}

        {/*
         * Two copies, because during an incident you want different things: the value, to
         * try it again, and the path, to say which field you mean. Revealed on hover so a
         * deep tree is not a wall of buttons.
         */}
        <span className="mvj-actions">
          <button
            type="button"
            title="Copy value"
            aria-label="Copy value"
            onClick={() => onCopy(container ? JSON.stringify(value, null, 2) : String(value))}
          >
            <Icon name="content_copy" size={13} />
          </button>
          {path && (
            <button
              type="button"
              title={`Copy path: ${path}`}
              aria-label="Copy path"
              onClick={() => onCopy(path)}
            >
              <Icon name="alternate_email" size={13} />
            </button>
          )}
        </span>
      </div>

      {container && open && (
        <div className="mvj-children">
          {visible.map(([key, child]) => (
            <Node
              key={key}
              name={key}
              value={child}
              path={path ? `${path}${Array.isArray(value) ? `[${key}]` : `.${key}`}` : String(key)}
              depth={depth + 1}
              term={term}
              openPaths={openPaths}
              toggle={toggle}
              onCopy={onCopy}
            />
          ))}

          {hidden > 0 && (
            <button
              type="button"
              className="mvj-more"
              onClick={() => setShown(current => current + _pageSize)}
            >
              Show {Math.min(hidden, _pageSize)} more of {entries.length}
            </button>
          )}

          <div className="mvj-close" style={{ '--depth': depth }}>
            {Array.isArray(value) ? ']' : '}'}
          </div>
        </div>
      )}
    </div>
  )
}

/* ── Viewer ──────────────────────────────────────────────────────────────── */

/** The three ways the body can be shown. `edit` only exists when `editable` is set. */
const VIEWS = { tree: 'tree', raw: 'raw', edit: 'edit' }

function JsonView({
  data,
  name = 'root',
  maxHeight = 420,
  editable = false,
  onChange = null,
  inputName = 'json',
  rows = 10,
  placeholder = '{\n  "key": "value"\n}',
  collapsible = false,
}) {
  const [term, setTerm] = useState('')
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(null)
  const [openPaths, setOpenPaths] = useState(() => new Set())

  /*
   * An editable field opens on the text, because that is what someone came to change. A
   * read-only payload opens on the tree, because that is what someone came to read.
   */
  const [view, setView] = useState(editable ? VIEWS.edit : VIEWS.tree)

  const parsed = useMemo(() => parseSafely(data), [data])

  const toggle = useCallback((path) => {
    setOpenPaths(current => {
      const next = new Set(current)

      if (next.has(path)) next.delete(path)
      else next.add(path)

      return next
    })
  }, [])

  /*
   * `navigator.clipboard` is only available on a secure origin, and this application is
   * routinely run over plain HTTP on a local network. The textarea fallback is what makes
   * copy work there rather than failing silently.
   */
  const copy = useCallback(async (text) => {
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(text)
      } else {
        const field = document.createElement('textarea')

        field.value = text
        field.setAttribute('readonly', '')
        field.style.position = 'fixed'
        field.style.opacity = '0'
        document.body.appendChild(field)
        field.select()
        document.execCommand('copy')
        document.body.removeChild(field)
      }

      setCopied(text.length > 40 ? 'copied' : text)
      setTimeout(() => setCopied(null), 1600)
    } catch (err) {
      console.error('Copy failed:', err)
    }
  }, [])

  const search = term.trim().toLowerCase()

  const hits = useMemo(() => {
    if (!search || !parsed.ok) return 0

    let count = 0

    const walk = (value, key, depth) => {
      if (depth > 12) return

      if (key !== undefined && String(key).toLowerCase().includes(search)) count += 1

      if (isContainer(value)) {
        childrenOf(value).forEach(([childKey, child]) => walk(child, childKey, depth + 1))
      } else if (String(value).toLowerCase().includes(search)) {
        count += 1
      }
    }

    walk(parsed.value, undefined, 0)

    return count
  }, [parsed, search])

  /*
   * Writing goes back out shaped like a DOM change event. The forms that consume this feed
   * every field through one `handleChange(e)`, so a bespoke signature would make this the
   * one control each of them has to special-case.
   */
  const emit = useCallback((text) => {
    onChange?.({ target: { name: inputName, value: text } })
  }, [onChange, inputName])

  const reformat = useCallback((indent) => {
    if (!parsed.ok) return

    try {
      emit(JSON.stringify(parsed.value, null, indent))
    } catch (err) {
      console.error('Reformat failed:', err)
    }
  }, [parsed, emit])

  // An empty read-only payload has nothing to show. An empty editable one is a field
  // waiting to be typed into, which is not the same thing.
  if (parsed.empty && !editable) {
    return <div className="mvj-empty">No data.</div>
  }

  const showing = editable && view === VIEWS.edit
    ? VIEWS.edit
    : parsed.ok && view === VIEWS.tree
      ? VIEWS.tree
      : VIEWS.raw

  const text = typeof data === 'string' ? data : parsed.raw
  const hasText = !!text && text.trim() !== ''

  /*
   * Collapsed, this is a single line rather than a panel.
   *
   * The execution log is a list of one-line messages, and attaching a full viewer to every
   * row that happens to carry data made those rows several times taller than the ones
   * without - the log stopped reading as a sequence. A row now says what it has and opens
   * on request.
   *
   * An empty object is worth saying out loud too: "log data {}" answers "did this line
   * carry anything?" without opening it, which a blank panel did not.
   */
  if (collapsible && !expanded) {
    const summary = parsed.ok && isContainer(parsed.value)
      ? summarise(parsed.value)
      : parsed.ok
        ? 'value'
        : 'text'

    return (
      <button type="button" className="mvj-peek" onClick={() => setExpanded(true)}>
        <Icon name="chevron_right" size={14} />
        <span className="mvj-peek-name">{name}</span>
        <span className="mvj-peek-summary">{summary}</span>
      </button>
    )
  }

  return (
    <div className={'mvj' + (editable && !parsed.ok && hasText ? ' has-error' : '')}>
      <div className="mvj-toolbar">
        {/* Search belongs to the tree. On raw text the browser's own find is better than a
            half-working one here, and in the editor it would compete with it. */}
        {showing === VIEWS.tree && (
          <div className="mvj-search">
            <Icon name="search" size={15} />
            <input
              type="text"
              value={term}
              onChange={e => setTerm(e.target.value)}
              placeholder="Find a key or value…"
              aria-label="Search JSON"
            />
            {term && (
              <>
                <span className="mvj-hits">{hits}</span>
                <button type="button" onClick={() => setTerm('')} aria-label="Clear search">
                  <Icon name="close" size={14} />
                </button>
              </>
            )}
          </div>
        )}

        {/*
         * Validity.
         *
         * Two different situations, and conflating them was a mistake.
         *
         * In a form the field is required to hold JSON, so anything that will not parse is
         * an error and is stated as one, in red, with the parser's message.
         *
         * In a read-only view it usually is not. An execution result is whatever the job
         * returned - often JSON, but just as legitimately a line of text, an id, or a
         * stack trace. Calling that "Invalid JSON — Unexpected token" accuses the payload
         * of being broken when nothing is broken. Here it is a quiet, uncoloured note that
         * the value is text rather than a document, and the text itself is shown either
         * way.
         */}
        {hasText && (editable || !parsed.ok) && (
          <span
            className={'mvj-validity ' + (parsed.ok ? 'is-valid' : editable ? 'is-invalid' : 'is-plain')}
            title={!parsed.ok && parsed.error ? parsed.error : undefined}
          >
            <Icon name={parsed.ok ? 'check_circle' : editable ? 'error' : 'subject'} size={14} />
            {parsed.ok ? 'Valid JSON' : editable ? 'Invalid JSON' : 'Not JSON — showing as text'}
          </span>
        )}

        <span className="mvj-spacer" />

        {/* Only offered when the panel can go back to being a line. */}
        {collapsible && (
          <button
            type="button"
            className="mvj-tool"
            onClick={() => setExpanded(false)}
            title="Collapse"
          >
            <Icon name="expand_less" size={15} />
            Hide
          </button>
        )}

        {/* The views. One control rather than a toggle, because with editing there are
            three of them and a two-state switch cannot express that. */}
        <div className="mvj-views" role="group" aria-label="View">
          {editable && (
            <button
              type="button"
              className={'mvj-view' + (showing === VIEWS.edit ? ' is-active' : '')}
              onClick={() => setView(VIEWS.edit)}
            >
              <Icon name="edit" size={14} />
              Edit
            </button>
          )}
          {/* The tree only exists for something that parsed. Hidden rather than disabled
              when there is nothing to switch to: on a plain-text result there is one view,
              and a permanently greyed control invites people to keep trying it. */}
          {parsed.ok && (
            <button
              type="button"
              className={'mvj-view' + (showing === VIEWS.tree ? ' is-active' : '')}
              onClick={() => setView(VIEWS.tree)}
              title="Show the document as a tree"
            >
              <Icon name="account_tree" size={14} />
              Tree
            </button>
          )}
          <button
            type="button"
            className={'mvj-view' + (showing === VIEWS.raw ? ' is-active' : '')}
            onClick={() => setView(VIEWS.raw)}
          >
            <Icon name={parsed.ok ? 'code' : 'subject'} size={14} />
            {parsed.ok ? 'Raw' : 'Text'}
          </button>
        </div>

        {editable && (
          <>
            <button
              type="button"
              className="mvj-tool"
              onClick={() => reformat(2)}
              disabled={!parsed.ok}
              title="Re-indent the document"
            >
              <Icon name="format_align_left" size={15} />
              Beautify
            </button>
            <button
              type="button"
              className="mvj-tool"
              onClick={() => reformat(0)}
              disabled={!parsed.ok}
              title="Strip whitespace"
            >
              <Icon name="compress" size={15} />
              Minify
            </button>
          </>
        )}

        <button type="button" className="mvj-tool" onClick={() => copy(text)} disabled={!hasText}>
          <Icon name={copied ? 'check' : 'content_copy'} size={15} />
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>

      {/*
       * The parser's message, and only while editing.
       *
       * There it earns its place: it names the character the parse gave up at, which is
       * what someone fixing the field needs. On a read-only value it was noise dressed as
       * an alarm - a result that is a line of text is not a malfunction, and a red strip
       * above it said otherwise. The chip in the toolbar covers that case quietly.
       */}
      {editable && !parsed.ok && hasText && (
        <div className="mvj-invalid">
          <Icon name="warning" size={16} />
          <span>{parsed.error || 'This is not valid JSON yet.'}</span>
        </div>
      )}

      {showing === VIEWS.edit ? (
        <textarea
          className="mvj-textarea"
          name={inputName}
          value={text}
          rows={rows}
          placeholder={placeholder}
          spellCheck={false}
          onChange={e => emit(e.target.value)}
        />
      ) : (
      <div className="mvj-body" style={{ maxHeight }}>
        {showing === VIEWS.tree ? (
          <Node
            name={undefined}
            value={parsed.value}
            path=""
            depth={0}
            term={search}
            openPaths={openPaths}
            toggle={toggle}
            onCopy={copy}
          />
        ) : (
          <pre className="mvj-raw">{parsed.ok ? parsed.raw : text}</pre>
        )}
      </div>
      )}

      {showing === VIEWS.tree && search && hits === 0 && (
        <div className="mvj-nohits">Nothing matches “{term}”.</div>
      )}

      <span className="mvj-name">{name}</span>
    </div>
  )
}

export default JsonView
