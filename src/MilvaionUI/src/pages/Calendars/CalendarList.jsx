import { useState, useEffect, useCallback } from 'react'
import { Link } from 'react-router-dom'
import calendarService from '../../services/calendarService'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import { useModal } from '../../hooks/useModal'
import { SkeletonTable } from '../../components/Skeleton'
import { getApiErrorMessage } from '../../utils/errorUtils'
import AuditInfoCard from '../../components/AuditInfoCard'
import TableActions, { ActionButton } from '../../components/TableActions'
import { TableToolbar, TableSearch, TableFooter } from '../../components/TableParts'
import { WEEKDAYS } from '../../utils/timeZones'
// Shared list-page styling (header, table, form modal) lives in RoleList.css, scoped under .role-list.
import '../UserManagement/RoleList.css'
import './CalendarList.css'

const emptyForm = () => ({ name: '', description: '', dates: [], dateRanges: [], weekdays: [] })

const summarize = (calendar) => {
  const parts = []
  if (calendar.weekdays?.length) parts.push(calendar.weekdays.map(d => WEEKDAYS[d].slice(0, 3)).join(', '))
  if (calendar.dateCount) parts.push(`${calendar.dateCount} date${calendar.dateCount === 1 ? '' : 's'}`)
  if (calendar.dateRangeCount) parts.push(`${calendar.dateRangeCount} range${calendar.dateRangeCount === 1 ? '' : 's'}`)
  return parts.join(' · ') || '—'
}

function CalendarList() {
  const [calendars, setCalendars] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [searchTerm, setSearchTerm] = useState('')
  const [debouncedSearchTerm, setDebouncedSearchTerm] = useState('')

  const [showFormModal, setShowFormModal] = useState(false)
  const [editing, setEditing] = useState(null)
  const [formData, setFormData] = useState(emptyForm())
  const [newDate, setNewDate] = useState('')
  const [newRange, setNewRange] = useState({ start: '', end: '', label: '' })
  const [formLoading, setFormLoading] = useState(false)
  const [formError, setFormError] = useState('')

  const { modalProps, showConfirm, showSuccess, showError } = useModal()

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearchTerm(searchTerm), 400)
    return () => clearTimeout(timer)
  }, [searchTerm])

  const loadCalendars = useCallback(async (showLoading = false) => {
    try {
      if (showLoading) setLoading(true)
      setError(null)

      const response = await calendarService.getAll(debouncedSearchTerm ? { searchTerm: debouncedSearchTerm } : {})
      setCalendars(response?.data?.data || response?.data || [])
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to load calendars'))
    } finally {
      if (showLoading) setLoading(false)
    }
  }, [debouncedSearchTerm])

  useEffect(() => {
    loadCalendars(true)
  }, [loadCalendars])

  const openCreate = () => {
    setEditing(null)
    setFormData(emptyForm())
    setNewDate('')
    setNewRange({ start: '', end: '', label: '' })
    setFormError('')
    setShowFormModal(true)
  }

  const openEdit = async (calendar) => {
    setFormError('')
    setNewDate('')
    setNewRange({ start: '', end: '', label: '' })

    try {
      const response = await calendarService.getById(calendar.id)
      const detail = response?.data || response

      setEditing(detail)
      setFormData({
        name: detail.name || '',
        description: detail.description || '',
        dates: detail.dates || [],
        dateRanges: (detail.dateRanges || []).map(r => ({ start: r.start, end: r.end, label: r.label || '' })),
        weekdays: detail.weekdays || []
      })
      setShowFormModal(true)
    } catch (err) {
      await showError(getApiErrorMessage(err, 'Failed to load the calendar'))
    }
  }

  const handleDelete = async (calendar) => {
    const confirmed = await showConfirm(
      `"${calendar.name}" will be deleted. A calendar that a job still uses cannot be deleted - remove it from those jobs first.`,
      'Delete Calendar',
      'Delete',
      'Cancel'
    )
    if (!confirmed) return

    try {
      const response = await calendarService.delete(calendar.id)
      if (response?.isSuccess === false) {
        await showError(response.messages?.[0]?.message || 'Failed to delete the calendar.')
        return
      }
      await loadCalendars(true)
      await showSuccess('Calendar deleted')
    } catch (err) {
      await showError(getApiErrorMessage(err, 'Failed to delete the calendar.'))
    }
  }

  const toggleWeekday = (day) => {
    setFormData(prev => ({
      ...prev,
      weekdays: prev.weekdays.includes(day) ? prev.weekdays.filter(d => d !== day) : [...prev.weekdays, day].sort()
    }))
  }

  const addDate = () => {
    if (!newDate || formData.dates.includes(newDate)) return
    setFormData(prev => ({ ...prev, dates: [...prev.dates, newDate].sort() }))
    setNewDate('')
  }

  const addRange = () => {
    if (!newRange.start || !newRange.end) {
      setFormError('A range needs a start and an end date.')
      return
    }
    if (newRange.end < newRange.start) {
      setFormError('A range must end on or after its start date.')
      return
    }
    setFormError('')
    setFormData(prev => ({ ...prev, dateRanges: [...prev.dateRanges, { ...newRange }].sort((a, b) => a.start.localeCompare(b.start)) }))
    setNewRange({ start: '', end: '', label: '' })
  }

  const handleSubmit = async () => {
    if (!formData.name.trim()) {
      setFormError('Calendar name is required.')
      return
    }

    if (formData.dates.length + formData.dateRanges.length + formData.weekdays.length === 0) {
      setFormError('Add at least one excluded date, date range or weekday.')
      return
    }

    setFormLoading(true)
    setFormError('')

    const payload = {
      name: formData.name.trim(),
      description: formData.description,
      dates: formData.dates,
      dateRanges: formData.dateRanges.map(r => ({ start: r.start, end: r.end, label: r.label || null })),
      weekdays: formData.weekdays
    }

    try {
      const response = editing ? await calendarService.update(editing.id, payload) : await calendarService.create(payload)

      if (response?.isSuccess === false) {
        setFormError(response.messages?.map(m => m.message).join(' ') || 'Failed to save the calendar.')
        return
      }

      setShowFormModal(false)
      loadCalendars(true)
      await showSuccess(editing ? 'Calendar updated' : 'Calendar created')
    } catch (err) {
      setFormError(getApiErrorMessage(err, 'Failed to save the calendar.'))
    } finally {
      setFormLoading(false)
    }
  }

  if (loading) return <SkeletonTable rows={8} columns={4} />
  if (error) return <div className="error">{error}</div>

  return (
    <div className="page role-list calendar-list">
      <Modal {...modalProps} />

      <div className="page-header">
        <h1>
          <Icon name="event_busy" size={28} />
          <span>Exclusion Calendars</span>
          <span>({calendars.length})</span>
        </h1>
        <button className="create-btn" onClick={openCreate}>
          <Icon name="add" size={18} />
          <span>Create Calendar</span>
        </button>
      </div>

      {calendars.length === 0 && !searchTerm ? (
        <div className="empty-state-card">
          <div className="empty-icon">
            <Icon name="event_busy" size={64} />
          </div>
          <h3>No Calendars Yet</h3>
          <p>
            A calendar lists days on which jobs should not run - public holidays, maintenance windows, weekends.
            Jobs that reference it skip those days and record the run as skipped.
          </p>
          <button className="empty-action-btn" onClick={openCreate}>Create Your First Calendar</button>
        </div>
      ) : (
        <div className="mv-table-card">
          <TableToolbar>
            <TableSearch value={searchTerm} onChange={setSearchTerm} placeholder="Search calendars…" />
          </TableToolbar>

          <div className="mv-table-scroll">
            <table className="mv-table">
              <thead>
                <tr>
                  <th>Calendar</th>
                  <th>Excludes</th>
                  <th className="mv-table-actions">Actions</th>
                </tr>
              </thead>
              <tbody>
                {calendars.map(calendar => (
                  <tr key={calendar.id} className="mv-table-row is-clickable" onClick={() => openEdit(calendar)}>
                    <td>
                      <span className="mv-table-primary">{calendar.name}</span>
                      {calendar.description && <span className="mv-table-muted">{calendar.description}</span>}
                    </td>
                    <td>{summarize(calendar)}</td>
                    <td className="mv-table-actions">
                      <TableActions>
                        <ActionButton intent="edit" icon="edit" title="Edit" onClick={() => openEdit(calendar)} />
                        <ActionButton intent="danger" icon="delete" title="Delete" onClick={() => handleDelete(calendar)} />
                      </TableActions>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <TableFooter totalCount={calendars.length} noun="calendars" />
        </div>
      )}

      {showFormModal && (
        <div className="modal-overlay" onClick={(e) => { if (e.target === e.currentTarget) setShowFormModal(false) }}>
          <div className="form-modal calendar-form-modal">
            <div className="form-modal-header">
              <h2>{editing ? 'Edit Calendar' : 'Create Calendar'}</h2>
              <button className="modal-close-btn" onClick={() => setShowFormModal(false)}>
                <Icon name="close" size={20} />
              </button>
            </div>

            <div className="form-modal-body">
              {formError && <div className="form-error"><Icon name="error" size={16} /><span>{formError}</span></div>}

              <div className="form-group">
                <label htmlFor="calendarName">Name *</label>
                <input
                  id="calendarName"
                  type="text"
                  className="form-input"
                  value={formData.name}
                  onChange={(e) => setFormData(prev => ({ ...prev, name: e.target.value }))}
                  placeholder="e.g. Public holidays 2027"
                />
              </div>

              <div className="form-group">
                <label htmlFor="calendarDescription">Description</label>
                <input
                  id="calendarDescription"
                  type="text"
                  className="form-input"
                  value={formData.description}
                  onChange={(e) => setFormData(prev => ({ ...prev, description: e.target.value }))}
                  placeholder="What is this calendar for?"
                />
              </div>

              <div className="form-group">
                <label>Weekdays</label>
                <div className="calendar-weekdays">
                  {WEEKDAYS.map((name, day) => (
                    <button
                      key={name}
                      type="button"
                      className={'calendar-weekday' + (formData.weekdays.includes(day) ? ' is-selected' : '')}
                      onClick={() => toggleWeekday(day)}
                      aria-pressed={formData.weekdays.includes(day)}
                    >
                      {name.slice(0, 3)}
                    </button>
                  ))}
                </div>
                <span className="field-hint">Every week, on these days, jobs using this calendar do not run.</span>
              </div>

              <div className="form-group">
                <label>Dates</label>
                <div className="calendar-add-row">
                  <input type="date" className="form-input" value={newDate} onChange={(e) => setNewDate(e.target.value)} />
                  <button type="button" className="calendar-add-btn" onClick={addDate} disabled={!newDate}>
                    <Icon name="add" size={16} /> Add
                  </button>
                </div>
                {formData.dates.length > 0 && (
                  <div className="calendar-chips">
                    {formData.dates.map(date => (
                      <span key={date} className="calendar-chip">
                        {date}
                        <button type="button" onClick={() => setFormData(prev => ({ ...prev, dates: prev.dates.filter(d => d !== date) }))} aria-label={`Remove ${date}`}>
                          <Icon name="close" size={14} />
                        </button>
                      </span>
                    ))}
                  </div>
                )}
              </div>

              <div className="form-group">
                <label>Date Ranges</label>
                <div className="calendar-add-row">
                  <input type="date" className="form-input" value={newRange.start} onChange={(e) => setNewRange(prev => ({ ...prev, start: e.target.value }))} aria-label="Range start" />
                  <input type="date" className="form-input" value={newRange.end} onChange={(e) => setNewRange(prev => ({ ...prev, end: e.target.value }))} aria-label="Range end" />
                  <input type="text" className="form-input" value={newRange.label} onChange={(e) => setNewRange(prev => ({ ...prev, label: e.target.value }))} placeholder="Label (optional)" />
                  <button type="button" className="calendar-add-btn" onClick={addRange}>
                    <Icon name="add" size={16} /> Add
                  </button>
                </div>
                {formData.dateRanges.length > 0 && (
                  <div className="calendar-chips">
                    {formData.dateRanges.map((range, index) => (
                      <span key={`${range.start}-${range.end}-${index}`} className="calendar-chip">
                        {range.start} → {range.end}{range.label ? ` · ${range.label}` : ''}
                        <button type="button" onClick={() => setFormData(prev => ({ ...prev, dateRanges: prev.dateRanges.filter((_, i) => i !== index) }))} aria-label="Remove range">
                          <Icon name="close" size={14} />
                        </button>
                      </span>
                    ))}
                  </div>
                )}
                <span className="field-hint">
                  Dates are calendar days. Each job checks them against the local date of its run in its own time zone.
                </span>
              </div>

              {editing?.jobs?.length > 0 && (
                <div className="form-group">
                  <label>Used by</label>
                  <div className="calendar-chips">
                    {editing.jobs.map(job => (
                      <Link key={job.id} to={`/jobs/${job.id}`} className="calendar-chip is-link">{job.displayName}</Link>
                    ))}
                  </div>
                </div>
              )}

              {editing?.auditInfo && <AuditInfoCard auditInfo={editing.auditInfo} inline />}
            </div>

            <div className="form-modal-footer">
              <button className="btn-cancel" onClick={() => setShowFormModal(false)}>Cancel</button>
              <button className="btn-submit" onClick={handleSubmit} disabled={formLoading}>
                {formLoading ? 'Saving...' : (editing ? 'Update Calendar' : 'Create Calendar')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default CalendarList
