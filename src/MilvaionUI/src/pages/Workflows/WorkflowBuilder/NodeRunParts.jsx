import Icon from '../../../components/Icon'

/**
 * Node kartlarının çalışma durumuyla ilgili ortak parçaları.
 *
 * Üç node tipi de aynı rozeti ve aynı meta satırını gösteriyor; ayrı ayrı yazıldığında
 * biri güncellenip diğerleri unutuluyordu.
 */

const statusLabels = {
  0: 'Pending',
  1: 'Running',
  2: 'Done',
  3: 'Failed',
  4: 'Skipped',
  5: 'Cancelled',
  6: 'Delayed',
}

const statusIcons = {
  0: 'hourglass_empty',
  1: 'sync',
  2: 'check_circle',
  3: 'error',
  4: 'skip_next',
  5: 'cancel',
  6: 'schedule',
}

function formatDuration(ms) {
  if (ms == null) return null
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`

  const minutes = Math.floor(ms / 60000)
  const seconds = Math.round((ms % 60000) / 1000)

  return `${minutes}m ${seconds}s`
}

/**
 * Kartın sağ üstündeki durum rozeti. Çalışmamış bir adım için "Idle" gösterir -
 * rozeti hiç göstermemek, adımı tanım ekranındaki gibi durumsuz gösterirdi.
 */
/* eslint-disable react/prop-types */
export function RunStatusBadge({ run }) {
  if (!run) {
    return (
      <span className="wfb-node-badge wfb-node-badge--idle">
        <span className="wfb-node-badge-dot" /> Idle
      </span>
    )
  }

  return (
    <span className={`wfb-node-badge wfb-node-badge--${run.status}`}>
      <Icon name={statusIcons[run.status] ?? 'help'} size={11} />
      {statusLabels[run.status] ?? 'Unknown'}
    </span>
  )
}

/**
 * Süre ve retry sayısı. Retry yalnızca sıfırdan büyükse gösteriliyor, çünkü her kartta
 * duran bir "0 retry" hiçbir şey anlatmadan yer kaplıyor.
 */
export function RunMetaRow({ run }) {
  if (!run) return null

  const duration = formatDuration(run.durationMs)
  const hasRetries = run.retryCount > 0

  if (!duration && !hasRetries && !run.error) return null

  return (
    <div className="wfb-node-meta">
      {duration && (
        <span className="wfb-node-tag">
          <Icon name="timer" size={11} /> {duration}
        </span>
      )}

      {hasRetries && (
        <span className="wfb-node-tag wfb-node-tag--warn">
          <Icon name="replay" size={11} /> {run.retryCount}
        </span>
      )}

      {run.error && (
        <span className="wfb-node-tag wfb-node-tag--error" title={run.error}>
          <Icon name="error" size={11} /> Error
        </span>
      )}
    </div>
  )
}

/**
 * Kartın altında duran dal etiketleri ("On Success", "On Failure").
 *
 * Bu etiketler eskiden kenarların üzerindeydi; uzaklaştırınca okunmuyor, kenarlar
 * kesiştiğinde hangi etiketin hangi kenara ait olduğu belirsizleşiyordu. Kaynak
 * düğümün içinde durduklarında ikisi de olmuyor.
 */
export function BranchLabels({ branches }) {
  if (!branches?.length) return null

  return (
    <div className="wfb-node-branches">
      {branches.map((branch, index) => (
        <span
          key={`${branch.label}-${index}`}
          className={`wfb-node-branch wfb-node-branch--${branch.tone ?? 'neutral'}`}
          title={branch.label}
        >
          {branch.label}
        </span>
      ))}
    </div>
  )
}
