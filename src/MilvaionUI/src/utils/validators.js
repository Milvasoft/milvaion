export const validateCronExpression = (cronExpression) => {
  if (!cronExpression || cronExpression.trim() === '') {
    return { isValid: false, error: 'Cron expression is required' }
  }

  const parts = cronExpression.trim().split(/\s+/)
  
  // Cron should have 5 or 6 parts (with or without seconds)
  if (parts.length !== 5 && parts.length !== 6) {
    return { 
      isValid: false, 
      error: 'Cron expression must have 5 or 6 parts (second minute hour day month dayOfWeek)' 
    }
  }

  return { isValid: true }
}

export const validateJobData = (jobData) => {
  if (!jobData || jobData.trim() === '') {
    return { isValid: true } // Empty is valid
  }

  try {
    JSON.parse(jobData)
    return { isValid: true }
  } catch (error) {
    return { 
      isValid: false, 
      error: 'Invalid JSON format' 
    }
  }
}

export const validateFutureDate = (date) => {
  if (!date) {
    return { isValid: false, error: 'Date is required' }
  }

  const targetDate = new Date(date)
  const now = new Date()

  if (targetDate <= now) {
    return { 
      isValid: false, 
      error: 'Date must be in the future' 
    }
  }

  return { isValid: true }
}
