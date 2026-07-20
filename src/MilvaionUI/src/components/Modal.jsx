import { useEffect } from 'react'
import PropTypes from 'prop-types'
import Icon from './Icon'
import './Modal.css'

function Modal({
  isOpen, 
  onClose, 
  onConfirm, 
  title, 
  message, 
  type = 'info',
  confirmText = 'OK',
  cancelText = 'Cancel',
  showCancel = false,
  className = '', // Add className prop
  /**
   * An optional second action beside the confirm button.
   *
   * `{ label, icon?, onClick }`. It exists so a dialog that reports something the user
   * created can offer to take them to it - "job triggered" leading to the execution it
   * started. Without it those dialogs could only say an id out loud and leave the user to
   * find the record themselves.
   *
   * The dialog closes after it runs, like confirm does; navigating away with the modal
   * still mounted would leave the backdrop over the destination.
   */
  extraAction = null
}) {
  useEffect(() => {
    const handleEscape = (e) => {
      if (e.key === 'Escape' && isOpen) {
        onClose()
      }
    }

    if (isOpen) {
      document.addEventListener('keydown', handleEscape)
      document.body.style.overflow = 'hidden'
    }

    return () => {
      document.removeEventListener('keydown', handleEscape)
      document.body.style.overflow = 'unset'
    }
  }, [isOpen, onClose])

  if (!isOpen) return null

  const getIcon = () => {
    switch (type) {
      case 'success':
        return 'check_circle'
      case 'warning':
        return 'warning'
      case 'error':
        return 'error'
      case 'confirm':
        return 'help'
      default:
        return 'info'
    }
  }

  const handleBackdropClick = (e) => {
    if (e.target === e.currentTarget) {
      onClose()
    }
  }

  const handleConfirm = () => {
    if (onConfirm) {
      onConfirm()
    }
    onClose()
  }

  return (
    <div className="modal-overlay" onClick={handleBackdropClick}>
      <div className={`modal-content modal-${type} ${className}`}> {/* ✅ Apply custom className */}
        <div className="modal-header">
          <div className="modal-icon">
            <Icon name={getIcon()} size={32} />
          </div>
          <h3 className="modal-title">{title}</h3>
          <button className="modal-close-btn" onClick={onClose}>
            <Icon name="close" size={20} />
          </button>
        </div>
        
        <div className="modal-body">
          <p className="modal-message">{message}</p>
        </div>
        
        <div className="modal-footer">
          {showCancel && (
            <button className="modal-btn modal-btn-cancel" onClick={onClose}>
              {cancelText}
            </button>
          )}
          {extraAction && (
            <button
              className="modal-btn modal-btn-extra"
              onClick={() => {
                extraAction.onClick?.()
                onClose()
              }}
            >
              {extraAction.icon && <Icon name={extraAction.icon} size={16} />}
              {extraAction.label}
            </button>
          )}
          <button
            className={`modal-btn modal-btn-confirm modal-btn-${type}`}
            onClick={handleConfirm}
          >
            {confirmText}
          </button>
        </div>
      </div>
    </div>
  )
}

Modal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onClose: PropTypes.func.isRequired,
  onConfirm: PropTypes.func,
  title: PropTypes.string.isRequired,
  message: PropTypes.oneOfType([PropTypes.string, PropTypes.node]).isRequired,
  type: PropTypes.oneOf(['info', 'success', 'warning', 'error', 'confirm', 'custom']),
  confirmText: PropTypes.string,
  cancelText: PropTypes.string,
  showCancel: PropTypes.bool,
  className: PropTypes.string,
  extraAction: PropTypes.shape({
    label: PropTypes.string.isRequired,
    icon: PropTypes.string,
    onClick: PropTypes.func
  })
}

export default Modal
