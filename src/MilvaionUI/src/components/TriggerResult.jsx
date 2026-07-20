import './TriggerResult.css'

/* eslint-disable react/prop-types */

/**
 * The body of a "something was triggered" dialog, and the config that builds one.
 *
 * Both trigger flows - a job and a workflow - end the same way: they report an id and
 * leave the user to find the record it names. They did it differently, though. The job
 * dialog printed the id in a box with hard-coded light colours, which on a dark theme was
 * a white strip across the middle; the workflow one concatenated it into a sentence. And
 * neither offered to open the thing it had just created, which is what the id is for.
 *
 * One shape for both, and a second button that goes there.
 */

/** The id, and any notes about how the run was started. */
export function TriggerResult({ label, id, notes = [] }) {
  return (
    <div className="trigger-result">
      <span className="trigger-result-label">{label}</span>
      <code className="trigger-result-id">{id || 'not returned'}</code>

      {notes.filter(Boolean).map(note => (
        <p key={note.text} className={`trigger-result-note is-${note.tone || 'info'}`}>
          {note.text}
        </p>
      ))}
    </div>
  )
}

/**
 * Builds the `showModal` config for a successful trigger.
 *
 * @param {string} title Dialog title.
 * @param {string} label What the id is - "Occurrence ID", "Run ID".
 * @param {string} id The identifier itself.
 * @param {string} goToLabel Text of the second button.
 * @param {string} to Route the second button opens.
 * @param {(to: string) => void} navigate
 * @param {{text: string, tone?: string}[]} notes
 * @param {() => void} onConfirm
 */
export function triggerResultModal({ title, label, id, goToLabel, to, navigate, notes = [], onConfirm }) {
  return {
    title,
    message: <TriggerResult label={label} id={id} notes={notes} />,
    confirmText: 'OK',
    // Offered only when there is somewhere to go. A button that navigates to
    // `/occurrences/undefined` is worse than no button.
    extraAction: id && to
      ? { label: goToLabel, icon: 'open_in_new', onClick: () => navigate(to) }
      : null,
    onConfirm,
  }
}
