import JsonView from './JsonView'
import './JsonEditor.css'

/* eslint-disable react/prop-types */

/**
 * A labelled JSON field.
 *
 * The editing behaviour now lives in `JsonView`, which is also what every read-only JSON
 * surface in the application uses. This is the form wrapper around it: label, required
 * marker, hint.
 *
 * The props are unchanged from the previous implementation - `value`, an `onChange` shaped
 * like a DOM change event, `name`, `rows`, `placeholder`, `required`, `label`, `hint` - so
 * the three screens that use it did not have to be touched. What they gain, without asking,
 * is everything the read-only viewer had and the old editor did not: a tree view of what
 * they are typing, search across it, and per-field copy. What the read-only screens gain in
 * return is the validity reporting that used to exist only here.
 */
function JsonEditor({
  value = '',
  onChange,
  name = 'json',
  placeholder = '{"key": "value"}',
  rows = 8,
  required = false,
  label,
  hint,
}) {
  return (
    <div className="json-editor-field">
      {label && (
        <label className="json-editor-label" htmlFor={name}>
          {label}
          {required && <span className="required">*</span>}
        </label>
      )}

      <JsonView
        data={value}
        name={name}
        editable
        onChange={onChange}
        inputName={name}
        rows={rows}
        placeholder={placeholder}
      />

      {hint && <small className="json-editor-hint">{hint}</small>}
    </div>
  )
}

export default JsonEditor
