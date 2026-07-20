import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import jobService from '../services/jobService'
import { useModal } from './useModal'
import { triggerResultModal } from '../components/TriggerResult'

/**
 * Custom hook for triggering jobs with modal feedback and force retry support.
 * Handles success/error states and concurrent policy violations.
 * 
 * @returns {Object} { triggerJob, triggering, modalProps }
 */
export function useTriggerJob() {
  const [triggering, setTriggering] = useState(false)
  const { modalProps, showModal } = useModal()
  const navigate = useNavigate()

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
        /*
         * The trigger command returns `Response<Guid>` and that Guid is the occurrence id -
         * the backend even creates it as `occurrenceId` and copies it into `CorrelationId`
         * because for a manual trigger the two are the same value.
         *
         * This read `response.data.id`, which is a property of an object that is not there:
         * `data` is the Guid itself. So the dialog has been showing an empty box, and the
         * `onSuccess` callback has been handing `undefined` to its callers. Both shapes are
         * accepted now so it keeps working if the payload is ever wrapped.
         */
        const occurrenceId = typeof response.data === 'string' ? response.data : response.data?.id

        showModal(triggerResultModal({
          title: force ? 'Force trigger successful' : 'Job triggered',
          // Named for what it is. It was labelled "Correlation ID" - the same value here,
          // but that name sends people looking for a different kind of record.
          label: 'Occurrence ID',
          id: occurrenceId,
          goToLabel: 'Go to occurrence',
          to: `/occurrences/${occurrenceId}`,
          navigate,
          notes: [
            jobData && { text: 'Custom job data was used for this execution.', tone: 'info' },
            force && { text: 'Concurrent policy checks were bypassed.', tone: 'warning' },
          ],
          onConfirm: () => {
            if (onSuccess) onSuccess(occurrenceId)
          }
        }))

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
