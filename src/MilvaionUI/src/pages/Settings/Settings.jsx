import { useEffect, useMemo, useState } from 'react'
import Icon from '../../components/Icon'
import settingsService from '../../services/settingsService'
import { useBranding } from '../../contexts/BrandingContext'
import { getApiErrorMessage } from '../../utils/errorUtils'
import './Settings.css'

/**
 * Turn a PascalCase alert type name into readable words, e.g.
 * "JobExecutionFailed" -> "Job Execution Failed".
 */
function humanize(name) {
  if (!name) return ''
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
}

/**
 * Application settings: branding text and per-notification routing. The logo and favicon are
 * static files and not managed here. Channel availability is read-only (from appsettings).
 */
function Settings() {
  const { refresh: refreshBranding } = useBranding()

  const [title, setTitle] = useState('')
  const [subtitle, setSubtitle] = useState('')
  const [notifications, setNotifications] = useState([])
  const [channels, setChannels] = useState([])
  const [availableChannels, setAvailableChannels] = useState([])

  const [open, setOpen] = useState({ branding: true, notifications: true })

  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    let cancelled = false

    async function load() {
      setLoading(true)
      try {
        const data = await settingsService.getSettings()
        if (cancelled) return
        setTitle(data?.branding?.title || '')
        setSubtitle(data?.branding?.subtitle || '')
        setNotifications(data?.notifications || [])
        setChannels(data?.channels || [])
        setAvailableChannels(data?.availableChannels || [])
        setError(null)
      } catch (err) {
        if (!cancelled) setError(getApiErrorMessage(err, 'Failed to load settings'))
      } finally {
        if (!cancelled) setLoading(false)
      }
    }

    load()
    return () => { cancelled = true }
  }, [])

  // Which channels are actually configured/enabled - used to flag routes that won't deliver.
  const channelEnabled = useMemo(() => {
    const map = {}
    for (const c of channels) map[c.name] = c.enabled
    return map
  }, [channels])

  function markDirty() {
    setSaved(false)
  }

  function toggleSection(key) {
    setOpen(prev => ({ ...prev, [key]: !prev[key] }))
  }

  function toggleEnabled(alertType) {
    setNotifications(prev => prev.map(n => n.alertType === alertType ? { ...n, enabled: !n.enabled } : n))
    markDirty()
  }

  function toggleChannel(alertType, channel) {
    setNotifications(prev => prev.map(n => {
      if (n.alertType !== alertType) return n
      const has = n.channels.includes(channel)
      return { ...n, channels: has ? n.channels.filter(c => c !== channel) : [...n.channels, channel] }
    }))
    markDirty()
  }

  async function handleSubmit(e) {
    e.preventDefault()
    setSaving(true)
    setSaved(false)
    setError(null)
    try {
      await settingsService.updateSettings({
        branding: { title: title.trim(), subtitle: subtitle.trim() },
        notifications: {
          rules: notifications.map(n => ({ alertType: n.alertType, enabled: n.enabled, channels: n.channels })),
        },
      })
      await refreshBranding()
      setSaved(true)
    } catch (err) {
      setError(getApiErrorMessage(err, 'Failed to save settings'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="page settings-page">
      <div className="page-header">
        <div className="page-title">
          <Icon name="settings" size={24} />
          <h1>Settings</h1>
        </div>
        <div className="page-header-actions">
          <button type="submit" form="settings-form" className="btn btn-primary" disabled={loading || saving}>
            {saving ? (<><Icon name="progress_activity" size={18} className="settings-spin" /> Saving...</>) : (<><Icon name="save" size={18} /> Save changes</>)}
          </button>
        </div>
      </div>

      <form id="settings-form" onSubmit={handleSubmit}>
        {/* Branding */}
        <section className="settings-card">
          <button type="button" className="settings-card-head" onClick={() => toggleSection('branding')} aria-expanded={open.branding}>
            <Icon name="badge" size={20} className="settings-card-icon" />
            <div className="settings-card-head-text">
              <h2>Branding</h2>
              <p>Shown in the browser tab, the sidebar and the login page. The logo is set from configuration files.</p>
            </div>
            <Icon name={open.branding ? 'expand_less' : 'expand_more'} size={22} className="settings-card-chevron" />
          </button>

          {open.branding && (
            <div className="settings-card-body">
              <div className="settings-field">
                <label htmlFor="brandingTitle">Title</label>
                <input id="brandingTitle" type="text" value={title} maxLength={100} placeholder="Milvaion"
                  onChange={(e) => { setTitle(e.target.value); markDirty() }} disabled={loading || saving} />
              </div>

              <div className="settings-field">
                <label htmlFor="brandingSubtitle">Subtitle</label>
                <input id="brandingSubtitle" type="text" value={subtitle} maxLength={200} placeholder="Job Scheduler & Workflow Engine"
                  onChange={(e) => { setSubtitle(e.target.value); markDirty() }} disabled={loading || saving} />
              </div>
            </div>
          )}
        </section>

        {/* Notifications */}
        <section className="settings-card">
          <button type="button" className="settings-card-head" onClick={() => toggleSection('notifications')} aria-expanded={open.notifications}>
            <Icon name="notifications" size={20} className="settings-card-icon" />
            <div className="settings-card-head-text">
              <h2>Notifications</h2>
              <p>Turn each alert on or off and choose which channels it goes to. Changes apply at runtime, across all instances.</p>
            </div>
            <Icon name={open.notifications ? 'expand_less' : 'expand_more'} size={22} className="settings-card-chevron" />
          </button>

          {open.notifications && (
            <div className="settings-card-body">
              {/* Channel status (read-only, from appsettings) */}
              <div className="settings-channels" role="list" aria-label="Channel status">
                {channels.map(ch => (
                  <span key={ch.name} role="listitem" className={'settings-channel ' + (ch.enabled ? 'is-on' : 'is-off')}
                    title={ch.enabled ? (ch.sendOnlyInProduction ? 'Active (production only)' : 'Active') : 'Not configured'}>
                    <span className="settings-channel-dot" />
                    {ch.name}
                  </span>
                ))}
              </div>
              <p className="settings-hint">Channel availability is configured in appsettings and shown here for reference. Routing an alert to a channel that is off will not deliver until that channel is enabled.</p>

              {/* Per-alert rules - name on top, channels below, uniform for every alert. */}
              <div className="settings-notif-list">
                {notifications.map(n => (
                  <div key={n.alertType} className={'settings-notif' + (n.enabled ? '' : ' is-disabled')}>
                    <div className="settings-notif-main">
                      <label className="settings-switch" title={n.enabled ? 'Enabled' : 'Disabled'}>
                        <input type="checkbox" checked={n.enabled} onChange={() => toggleEnabled(n.alertType)} disabled={loading || saving} />
                        <span className="settings-switch-track" />
                      </label>
                      <span className="settings-notif-name">{humanize(n.alertTypeName)}</span>
                    </div>

                    <div className="settings-chip-row">
                      {availableChannels.map(c => {
                        const active = n.channels.includes(c)
                        const off = channelEnabled[c] === false
                        return (
                          <button key={c} type="button"
                            className={'settings-chip' + (active ? ' is-active' : '') + (off ? ' is-muted' : '')}
                            onClick={() => toggleChannel(n.alertType, c)}
                            disabled={!n.enabled || loading || saving}
                            title={off ? `${c} is not enabled in configuration` : c}>
                            {active && <Icon name="check" size={14} />}
                            {c}
                          </button>
                        )
                      })}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </section>

        {error && (
          <div className="settings-result settings-result--error">
            <Icon name="error" size={18} />
            <span>{error}</span>
          </div>
        )}

        {saved && !error && (
          <div className="settings-result settings-result--success">
            <Icon name="check_circle" size={18} />
            <span>Saved. Changes apply across all instances.</span>
          </div>
        )}
      </form>
    </div>
  )
}

export default Settings
