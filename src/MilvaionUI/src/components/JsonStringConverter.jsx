import { useState } from 'react'
import Icon from './Icon'
import './JsonStringConverter.css'

/**
 * A helper component that converts any text to a JSON-escaped string.
 * Useful for embedding content with special characters (quotes, SQL, etc.) inside JSON fields.
 */
function JsonStringConverter() {
  const [input, setInput] = useState('')
  const [output, setOutput] = useState('')
  const [copied, setCopied] = useState(false)
  const [expanded, setExpanded] = useState(false)

  const handleConvert = () => {
    setCopied(false)

    if (!input) {
      setOutput('')
      return
    }

    // Convert to JSON string and remove outer quotes
    // This escapes all special characters: quotes, newlines, tabs, etc.
    const escaped = JSON.stringify(input)

    // Remove outer quotes since user will paste inside quotes
    setOutput(escaped.slice(1, -1))
  }

  const handleCopy = async () => {
    if (!output) return

    try {
      await navigator.clipboard.writeText(output)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch (e) {
      console.error('Failed to copy:', e)
    }
  }

  const handleClear = () => {
    setInput('')
    setOutput('')
    setCopied(false)
  }

  return (
    <div className="sidebar-card json-string-converter">
      <h4
        className="sidebar-card-title clickable"
        onClick={() => setExpanded(!expanded)}
      >
        <Icon name="data_object" size={18} />
        Text → JSON String
        <Icon
          name={expanded ? 'expand_less' : 'expand_more'}
          size={18}
          className="expand-icon"
        />
      </h4>

      {expanded && (
        <div className="converter-content">
          <p className="converter-description">
            Convert any text (SQL, JSON, etc.) to a properly escaped JSON string value.
          </p>

          <div className="converter-input-group">
            <label>Input (Any Text)</label>
            <textarea
              value={input}
              onChange={(e) => setInput(e.target.value)}
              placeholder={'select * from "Users" where name = \'John\''}
              rows={4}
            />
          </div>

          <div className="converter-actions">
            <button
              type="button"
              className="btn btn-sm btn-primary"
              onClick={handleConvert}
            >
              <Icon name="transform" size={14} />
              Convert
            </button>
            <button
              type="button"
              className="btn btn-sm btn-secondary"
              onClick={handleClear}
            >
              <Icon name="clear" size={14} />
              Clear
            </button>
          </div>

          {output && (
            <div className="converter-output-group">
              <label>
                Output (Escaped String)
                <button
                  type="button"
                  className="copy-btn"
                  onClick={handleCopy}
                  title="Copy to clipboard"
                >
                  <Icon name={copied ? 'check' : 'content_copy'} size={14} />
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              </label>
              <textarea
                value={output}
                readOnly
                rows={4}
                className="output-textarea"
              />
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default JsonStringConverter
