import { useState, useEffect, useCallback } from 'react'
import Icon from './Icon'
import './JsonEditor.css'

/**
 * JSON Editor component with beautify and validation features.
 * 
 * @param {Object} props
 * @param {string} props.value - JSON string value
 * @param {Function} props.onChange - Callback when value changes (receives event-like object or string)
 * @param {string} props.name - Input name for form handling
 * @param {string} props.placeholder - Placeholder text
 * @param {number} props.rows - Number of textarea rows
 * @param {boolean} props.required - Whether field is required
 * @param {string} props.label - Optional label text
 * @param {string} props.hint - Optional hint text below input
 */
function JsonEditor({ 
  value = '', 
  onChange, 
  name = 'json', 
  placeholder = '{"key": "value"}',
  rows = 8,
  required = false,
  label,
  hint
}) {
  const [error, setError] = useState(null)
  const [isValid, setIsValid] = useState(true)

  // Validate JSON on value change
  const validateJson = useCallback((jsonString) => {
    if (!jsonString || jsonString.trim() === '') {
      setError(null)
      setIsValid(true)
      return true
    }

    try {
      JSON.parse(jsonString)
      setError(null)
      setIsValid(true)
      return true
    } catch (e) {
      setError(e.message)
      setIsValid(false)
      return false
    }
  }, [])

  // Validate on mount and value change
  useEffect(() => {
    validateJson(value)
  }, [value, validateJson])

  // Handle input change
  const handleChange = (e) => {
    const newValue = e.target.value
    validateJson(newValue)
    
    // Call onChange with event-like object for compatibility
    if (onChange) {
      onChange({
        target: {
          name: name,
          value: newValue
        }
      })
    }
  }

  // Beautify JSON
  const handleBeautify = () => {
    if (!value || value.trim() === '') return

    try {
      const parsed = JSON.parse(value)
      const beautified = JSON.stringify(parsed, null, 2)
      
      if (onChange) {
        onChange({
          target: {
            name: name,
            value: beautified
          }
        })
      }
      setError(null)
      setIsValid(true)
    } catch (e) {
      setError(e.message)
      setIsValid(false)
    }
  }

  // Minify JSON
  const handleMinify = () => {
    if (!value || value.trim() === '') return

    try {
      const parsed = JSON.parse(value)
      const minified = JSON.stringify(parsed)
      
      if (onChange) {
        onChange({
          target: {
            name: name,
            value: minified
          }
        })
      }
      setError(null)
      setIsValid(true)
    } catch (e) {
      setError(e.message)
      setIsValid(false)
    }
  }

  // Copy to clipboard
  const handleCopy = async () => {
    if (!value) return
    
    try {
      await navigator.clipboard.writeText(value)
    } catch (e) {
      console.error('Failed to copy:', e)
    }
  }

  return (
    <div className={`json-editor ${!isValid ? 'has-error' : ''}`}>
      {label && (
        <label className="json-editor-label">
          {label}
          {required && <span className="required">*</span>}
        </label>
      )}
      
      <div className="json-editor-container">
        <div className="json-editor-toolbar">
          <div className="toolbar-left">
            {isValid && value && value.trim() !== '' && (
              <span className="validation-badge valid">
                <Icon name="check_circle" size={14} />
                Valid JSON
              </span>
            )}
            {!isValid && (
              <span className="validation-badge invalid">
                <Icon name="error" size={14} />
                Invalid JSON
              </span>
            )}
          </div>
          <div className="toolbar-right">
            <button
              type="button"
              className="toolbar-btn"
              onClick={handleBeautify}
              title="Beautify JSON"
              disabled={!value || value.trim() === ''}
            >
              <Icon name="format_align_left" size={16} />
              <span>Beautify</span>
            </button>
            <button
              type="button"
              className="toolbar-btn"
              onClick={handleMinify}
              title="Minify JSON"
              disabled={!value || value.trim() === ''}
            >
              <Icon name="compress" size={16} />
              <span>Minify</span>
            </button>
            <button
              type="button"
              className="toolbar-btn"
              onClick={handleCopy}
              title="Copy to clipboard"
              disabled={!value || value.trim() === ''}
            >
              <Icon name="content_copy" size={16} />
            </button>
          </div>
        </div>
        
        <textarea
          id={name}
          name={name}
          value={value}
          onChange={handleChange}
          rows={rows}
          className={`json-editor-textarea ${!isValid ? 'invalid' : ''}`}
          placeholder={placeholder}
          spellCheck={false}
          required={required}
        />
        
        {error && (
          <div className="json-editor-error">
            <Icon name="error_outline" size={14} />
            <span>{error}</span>
          </div>
        )}
      </div>
      
      {hint && <small className="json-editor-hint">{hint}</small>}
    </div>
  )
}

export default JsonEditor
