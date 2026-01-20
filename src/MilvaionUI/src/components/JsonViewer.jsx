import { useState } from 'react'
import Icon from './Icon'
import './JsonViewer.css'

// Simple JSON syntax highlighter
function highlightJson(json) {
  if (!json) return ''
  
  // Escape HTML first
  const escaped = json
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
  
  // Apply syntax highlighting
  return escaped
    // Strings (including keys) - but we'll handle keys separately
    .replace(/"([^"\\]*(\\.[^"\\]*)*)"/g, (match, content) => {
      // Check if this is followed by a colon (it's a key)
      return `<span class="json-string">"${content}"</span>`
    })
    // Numbers
    .replace(/\b(-?\d+\.?\d*)\b/g, '<span class="json-number">$1</span>')
    // Booleans
    .replace(/\b(true|false)\b/g, '<span class="json-boolean">$1</span>')
    // Null
    .replace(/\bnull\b/g, '<span class="json-null">null</span>')
    // Keys (strings followed by colon)
    .replace(/<span class="json-string">"([^"]+)"<\/span>(\s*):/g, 
      '<span class="json-key">"$1"</span>$2:')
}

function JsonViewer({ data, title = 'JSON Data', defaultExpanded = false }) {
  const [isExpanded, setIsExpanded] = useState(defaultExpanded)
  const [copySuccess, setCopySuccess] = useState(false)

  if (!data) return null

  let formattedJson
  try {
    const parsedData = typeof data === 'string' ? JSON.parse(data) : data
    formattedJson = JSON.stringify(parsedData, null, 2)
  } catch {
    formattedJson = typeof data === 'string' ? data : String(data)
  }

  const handleCopy = () => {
    navigator.clipboard.writeText(formattedJson)
    setCopySuccess(true)
    setTimeout(() => setCopySuccess(false), 2000)
  }

  // Get preview (first line or truncated)
  const getPreview = () => {
    if (!formattedJson) return ''
    const firstLine = formattedJson.split('\n')[0]
    if (formattedJson.length <= 50) return formattedJson
    return firstLine.length > 50 ? firstLine.substring(0, 50) + '...' : firstLine + '...'
  }

  return (
    <div className="json-viewer">
      <div className="json-viewer-header">
        <button
          className="json-toggle"
          onClick={() => setIsExpanded(!isExpanded)}
          title={isExpanded ? 'Collapse' : 'Expand'}
        >
          <Icon name={isExpanded ? 'expand_less' : 'expand_more'} size={20} />
          <span>{title}</span>
          {!isExpanded && <span className="json-preview">{getPreview()}</span>}
        </button>
        <button
          className="json-copy-btn"
          onClick={handleCopy}
          title={copySuccess ? 'Copied!' : 'Copy to clipboard'}
        >
          <Icon name={copySuccess ? 'check' : 'content_copy'} size={18} />
          {copySuccess ? 'Copied' : 'Copy'}
        </button>
      </div>

      {isExpanded && (
        <pre 
          className="json-viewer-content"
          dangerouslySetInnerHTML={{ __html: highlightJson(formattedJson) }}
        />
      )}
    </div>
  )
}

export default JsonViewer
