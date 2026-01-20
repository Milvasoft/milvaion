import { useState, useCallback } from 'react'

export function useModal() {
  const [modal, setModal] = useState({
    isOpen: false,
    title: '',
    message: '',
    type: 'info',
    confirmText: 'OK',
    cancelText: 'Cancel',
    showCancel: false,
    onConfirm: null,
    className: '' // Add className state
  })

  const showModal = useCallback((messageOrConfig, title, confirmText, cancelText) => {
    return new Promise((resolve) => {
      // Support both formats:
      // 1. showModal({ message, title, confirmText, cancelText, ... })
      // 2. showModal(message, title, confirmText, cancelText)
      let config
      if (typeof messageOrConfig === 'object' && messageOrConfig.message !== undefined) {
        // Object format
        config = messageOrConfig
      } else {
        // Positional arguments format
        config = {
          message: messageOrConfig,
          title: title || 'Modal',
          confirmText: confirmText || 'OK',
          cancelText: cancelText || 'Cancel',
          showCancel: !!cancelText,
          type: 'custom'
        }
      }

      setModal({
        isOpen: true,
        title: config.title,
        message: config.message,
        type: config.type || 'custom',
        confirmText: config.confirmText || 'OK',
        cancelText: config.cancelText || 'Cancel',
        showCancel: config.showCancel !== undefined ? config.showCancel : !!config.cancelText,
        className: config.className || '', // Support custom className
        onConfirm: () => {
          if (config.onConfirm) config.onConfirm()
          resolve(true)
          closeModal()
        },
        onCancel: () => { // Add onCancel handler
          resolve(false)
          closeModal()
        }
      })
    })
  }, [])

  const showAlert = useCallback((message, title = 'Alert', type = 'info') => {
    return showModal({
      title,
      message,
      type,
      confirmText: 'OK',
      showCancel: false
    })
  }, [showModal])

  const showSuccess = useCallback((message, title = 'Success') => {
    return showAlert(message, title, 'success')
  }, [showAlert])

  const showError = useCallback((message, title = 'Error') => {
    return showAlert(message, title, 'error')
  }, [showAlert])

  const showWarning = useCallback((message, title = 'Warning') => {
    return showAlert(message, title, 'warning')
  }, [showAlert])

  const showConfirm = useCallback((message, title = 'Confirm', confirmText = 'Yes', cancelText = 'No') => {
    return new Promise((resolve) => {
      setModal({
        isOpen: true,
        title,
        message,
        type: 'confirm',
        confirmText,
        cancelText,
        showCancel: true,
        onConfirm: () => {
          resolve(true)
          closeModal()
        },
        onCancel: () => { // Add onCancel for showConfirm too
          resolve(false)
          closeModal()
        }
      })
    })
  }, [])

  const closeModal = useCallback(() => {
    setModal(prev => ({ ...prev, isOpen: false }))
  }, [])

  return {
    modalProps: {
      isOpen: modal.isOpen,
      onClose: modal.onCancel || closeModal, // Use onCancel if available
      onConfirm: modal.onConfirm,
      title: modal.title,
      message: modal.message,
      type: modal.type,
      confirmText: modal.confirmText,
      cancelText: modal.cancelText,
      showCancel: modal.showCancel,
      className: modal.className // Pass className to modal
    },
    showModal,
    showAlert,
    showSuccess,
    showError,
    showWarning,
    showConfirm,
    closeModal
  }
}
