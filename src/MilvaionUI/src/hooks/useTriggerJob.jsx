import { useState } from 'react'
import jobService from '../services/jobService'
import { useModal } from './useModal'

/**
 * Custom hook for triggering jobs with modal feedback and force retry support.
 * Handles success/error states and concurrent policy violations.
 * 
 * @returns {Object} { triggerJob, triggering, modalProps }
 */
export function useTriggerJob() {
  const [triggering, setTriggering] = useState(false)
  const { modalProps, showModal } = useModal()

  /**
   * Triggers a job with optional force parameter and custom job data.
   * Shows success/error modals and handles concurrent policy violations.
   * 
   * @param {string} jobId - Job ID to trigger
   * @param {string} reason - Reason for triggering (default: 'Manual trigger by user')
   * @param {boolean} force - Force trigger bypassing concurrent policy (default: false)
   * @param {string|null} jobData - Optional custom job data JSON (default: null, uses job's existing data)
   * @param {Function} onSuccess - Optional callback on success (receives correlationId)
   * @returns {Promise<boolean>} True if successful, false otherwise
   */
  const triggerJob = async (jobId, reason = 'Manual trigger by user', force = false, jobData = null, onSuccess = null) => {
    if (triggering) return false

    setTriggering(true)

    try {
      const response = await jobService.trigger(jobId, reason, force, jobData)

      if (response.success || response.isSuccess) {
        const correlationId = response.data

        showModal({
          title: force ? '⚡ Force Trigger Successful' : '✅ Job Triggered Successfully',
          message: (
            <div style={{ textAlign: 'left' }}>
              <p><strong>Correlation ID:</strong></p>
              <code style={{
                display: 'block',
                padding: '8px',
                background: '#f5f5f5',
                color: '#333',
                borderRadius: '4px',
                marginBottom: '12px',
                wordBreak: 'break-all'
              }}>
                {correlationId}
              </code>
              {jobData && (
                <p style={{ color: '#2196f3', fontSize: '14px' }}>
                  ℹ️ Custom job data was used for this execution.
                </p>
              )}
              {force && (
                <p style={{ color: '#ff9800', fontSize: '14px' }}>
                  ⚠️ Concurrent policy checks were bypassed.
                </p>
              )}
              <p style={{ fontSize: '14px', color: '#666' }}>
                Check the execution history for progress.
              </p>
            </div>
          ),
          confirmText: 'OK',
          onConfirm: () => {
            if (onSuccess) onSuccess(correlationId)
          }
        })

        return true
      } else {
        const errorMessage = response.message ||
          response.messages?.[0]?.message ||
          'Failed to trigger job'

        // Check if it's a concurrent policy violation
        if (errorMessage.includes('ConcurrentExecutionPolicy') ||
          errorMessage.includes('MaxConcurrent') ||
          errorMessage.includes('already has')) {

          // Show force trigger confirmation modal
          showModal({
            title: '⚠️ Concurrent Policy Violation',
            message: (
              <div style={{ textAlign: 'left' }}>
                <p>{errorMessage}</p>
                <br />
                <p><strong>Do you want to FORCE trigger this job?</strong></p>
                <p style={{ fontSize: '14px', color: '#666' }}>
                  This will bypass concurrent policy checks and start the job immediately.
                </p>
              </div>
            ),
            confirmText: 'Force Trigger',
            cancelText: 'Cancel',
            showCancel: true,
            onConfirm: () => triggerJob(jobId, reason, true, jobData, onSuccess)
          })
        } else {
          // Show regular error modal
          showModal({
            title: '❌ Trigger Failed',
            message: errorMessage,
            confirmText: 'OK'
          })
        }

        return false
      }
    } catch (err) {
      console.error('Trigger error:', err)

      const errorMessage = err.response?.data?.message ||
        err.response?.data?.messages?.[0]?.message ||
        err.message ||
        'Failed to trigger job'

      // Check if it's a concurrent policy error
      if (errorMessage.includes('ConcurrentExecutionPolicy') ||
        errorMessage.includes('MaxConcurrent') ||
        errorMessage.includes('already has')) {

        showModal({
          title: '⚠️ Concurrent Policy Violation',
          message: (
            <div style={{ textAlign: 'left' }}>
              <p>{errorMessage}</p>
              <br />
              <p><strong>Do you want to FORCE trigger this job?</strong></p>
              <p style={{ fontSize: '14px', color: '#666' }}>
                This will bypass concurrent policy checks and start the job immediately.
              </p>
            </div>
          ),
          confirmText: 'Force Trigger',
          cancelText: 'Cancel',
          showCancel: true,
          onConfirm: () => triggerJob(jobId, reason, true, jobData, onSuccess)
        })
      } else {
        showModal({
          title: '❌ Trigger Failed',
          message: errorMessage,
          confirmText: 'OK'
        })
      }

      return false
    } finally {
      setTriggering(false)
    }
  }

  return {
    triggerJob,
    triggering,
    modalProps
  }
}
