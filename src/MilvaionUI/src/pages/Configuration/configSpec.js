/**
 * What the configuration page shows, as data.
 *
 * The page this replaces wrote every setting out by hand: five hundred lines of JSX in
 * which each row repeated the same three spans, and the only difference between two rows
 * was a string. Describing the settings instead is what makes search, the section index
 * and the "what is switched off" summary possible - written against the old markup, each
 * of those would have meant walking the JSX by hand or listing all ninety settings again.
 *
 * A row is `{ label, value, kind, unit?, hint?, on?, off? }`.
 *
 *   text   the default - a number or a short string
 *   code   an identifier: hostname, key prefix, queue name, connection string
 *   bool   a switch
 *
 * Rows whose value is `undefined` are dropped, so a section can list settings that only
 * some deployments report without every caller having to guard.
 */

/* ── Row helpers ─────────────────────────────────────────────────────────── */

const text = (label, value, unit, hint) => ({ kind: 'text', label, value, unit, hint })
const code = (label, value, hint) => ({ kind: 'code', label, value, hint })
const bool = (label, value, hint, on, off) => ({ kind: 'bool', label, value, hint, on, off })

/** Drops rows the API did not send, so callers can list optional settings unguarded. */
const rows = (...list) => list.filter(row => row && row.value !== undefined)

const group = (title, ...rowList) => ({ title, rows: rows(...rowList) })

/* ── Formatting ──────────────────────────────────────────────────────────── */

/**
 * .NET sends a TimeSpan as "1.02:30:45.123". Rendered down to two units: on a page this
 * dense, "1d 2h" is read and "1.02:30:45.1230000" is skipped.
 */
export function formatUptime(uptime) {
  if (!uptime) return 'unknown'
  if (typeof uptime !== 'string') return String(uptime)

  const parts = uptime.split(':')

  if (parts.length < 3) return uptime

  const head = parts[0].split('.')
  const days = head.length > 1 ? parseInt(head[0], 10) : 0
  const hours = parseInt(head[head.length - 1], 10)
  const minutes = parseInt(parts[1], 10)

  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`

  return `${minutes}m`
}

/* ── Identity ────────────────────────────────────────────────────────────── */

/** The header strip: which system this is, and how the host is doing. */
export function systemIdentity(config) {
  return {
    environment: config.environment,
    version: config.version,
    hostName: config.hostName,
    uptime: formatUptime(config.uptime),
    startupTime: config.startupTime,
    resources: config.systemResources || null,
  }
}

/* ── Switches that are off ───────────────────────────────────────────────── */

/**
 * Everything on the page that can be switched off, and is.
 *
 * Deliberately only the things whose being off changes behaviour someone would come here
 * to explain. `durable`, `autoDelete` and `sendOnlyInProduction` are also booleans, but
 * "off" is a normal setting for them rather than a symptom, and listing them would make
 * this line noise on a healthy system.
 */
export function offSwitches(config) {
  const bg = config.backgroundServices || {}

  const candidates = [
    ['Job dispatcher', config.jobDispatcher?.enabled],
    ['Startup recovery', config.jobDispatcher?.enableStartupRecovery],
    ['Job auto-disable', config.jobAutoDisable?.enabled],
    ['Worker auto-discovery', bg.workerAutoDiscovery?.enabled],
    ['Zombie detector', bg.zombieOccurrenceDetector?.enabled],
    ['Log collector', bg.logCollector?.enabled],
    ['Status tracker', bg.statusTracker?.enabled],
    ['Failed occurrence handler', bg.failedOccurrenceHandler?.enabled],
    ['External job tracker', bg.externalJobTracker?.enabled],
    ['Workflow engine', bg.workflowEngine?.enabled],
    ['Seq', config.observability?.seq?.enabled],
    ['OpenTelemetry', config.observability?.openTelemetry?.enabled],
  ]

  return candidates.filter(([, enabled]) => enabled === false).map(([name]) => name)
}

/* ── Sections ────────────────────────────────────────────────────────────── */

/**
 * The whole page.
 *
 * Ordered by how often it answers a question rather than by subsystem: dispatching and
 * the background services first, because that is what "why isn't my job running" resolves
 * to, and the infrastructure addresses last, because those are looked up rather than read.
 */
export function buildSections(config) {
  const bg = config.backgroundServices || {}
  const sections = []

  /* Dispatching ---------------------------------------------------------- */

  sections.push({
    id: 'dispatch',
    icon: 'rocket_launch',
    title: 'Dispatching',
    note: 'How often the dispatcher looks for due work, and how much it takes at a time.',
    groups: [
      group(
        null,
        bool('Dispatcher', config.jobDispatcher?.enabled),
        text('Polling interval', config.jobDispatcher?.pollingIntervalSeconds, 's',
          'How long the dispatcher waits between checks for due jobs.'),
        text('Batch size', config.jobDispatcher?.batchSize, null,
          'Jobs claimed per pass.'),
        text('Lock TTL', config.jobDispatcher?.lockTtlSeconds, 's',
          'How long a claim is held before another instance may take the job.'),
        bool('Startup recovery', config.jobDispatcher?.enableStartupRecovery, null,
          'On', 'Off'),
      ),
    ],
  })

  if (config.jobAutoDisable) {
    sections.push({
      id: 'auto-disable',
      icon: 'power_off',
      title: 'Auto-disable',
      note: `A job that fails ${config.jobAutoDisable.consecutiveFailureThreshold} times in a row within `
        + `${config.jobAutoDisable.failureWindowMinutes} minutes is switched off automatically, and `
        + (config.jobAutoDisable.autoReEnableAfterCooldown
          ? `switched back on after ${config.jobAutoDisable.autoReEnableCooldownMinutes} minutes.`
          : 'stays off until someone re-enables it from the Jobs page.'),
      groups: [
        group(
          null,
          bool('Auto-disable', config.jobAutoDisable.enabled),
          text('Failure threshold', config.jobAutoDisable.consecutiveFailureThreshold, ' failures'),
          text('Failure window', config.jobAutoDisable.failureWindowMinutes, ' min'),
          bool('Re-enable after cooldown', config.jobAutoDisable.autoReEnableAfterCooldown, null, 'Yes', 'No'),
          text('Cooldown', config.jobAutoDisable.autoReEnableCooldownMinutes, ' min'),
        ),
      ],
    })
  }

  /* Background services --------------------------------------------------- */

  if (config.backgroundServices) {
    sections.push({
      id: 'services',
      icon: 'settings_suggest',
      title: 'Background services',
      note: 'Services running inside the API process. A job that is never dispatched, or logs '
        + 'that arrive late, usually trace back to one of these being off or polling slowly.',
      groups: [
        group('Worker auto-discovery',
          bool('Service', bg.workerAutoDiscovery?.enabled),
        ),
        group('Zombie occurrence detector',
          bool('Service', bg.zombieOccurrenceDetector?.enabled),
          text('Check interval', bg.zombieOccurrenceDetector?.checkIntervalSeconds, 's'),
          text('Zombie timeout', bg.zombieOccurrenceDetector?.zombieTimeoutMinutes, ' min',
            'An execution with no heartbeat for this long is treated as dead.'),
        ),
        group('Log collector',
          bool('Service', bg.logCollector?.enabled),
          text('Batch size', bg.logCollector?.batchSize),
          text('Batch interval', bg.logCollector?.batchIntervalMs, 'ms'),
        ),
        group('Status tracker',
          bool('Service', bg.statusTracker?.enabled),
          text('Batch size', bg.statusTracker?.batchSize),
          text('Batch interval', bg.statusTracker?.batchIntervalMs, 'ms'),
          text('Execution log limit', bg.statusTracker?.executionLogMaxCount, ' lines',
            'Lines kept per execution. Older lines are dropped.'),
        ),
        group('Failed occurrence handler',
          bool('Service', bg.failedOccurrenceHandler?.enabled),
        ),
        group('External job tracker',
          bool('Service', bg.externalJobTracker?.enabled),
          text('Registration batch size', bg.externalJobTracker?.registrationBatchSize),
          text('Occurrence batch size', bg.externalJobTracker?.occurrenceBatchSize),
          text('Batch interval', bg.externalJobTracker?.batchIntervalMs, 'ms'),
        ),
        group('Workflow engine',
          bool('Service', bg.workflowEngine?.enabled),
          text('Polling interval', bg.workflowEngine?.pollingIntervalSeconds, 's'),
        ),
      ].filter(g => g.rows.length > 0),
    })
  }

  /* Alerting -------------------------------------------------------------- */

  if (config.alerting) {
    const channels = config.alerting.channels || []

    sections.push({
      id: 'alerting',
      icon: 'notifications_active',
      title: 'Alerting',
      note: 'Channel status only. Webhook URLs and SMTP credentials are deliberately not exposed here.',
      groups: [
        group(
          null,
          code('App url', config.alerting.milvaionAppUrl,
            'Used to build links in outgoing alerts.'),
          text('Default channel', config.alerting.defaultChannel),
          bool('Production only', config.alerting.sendOnlyInProduction, null, 'Yes', 'No'),
          text('Alert types', config.alerting.enabledAlertCount != null
            ? `${config.alerting.enabledAlertCount} of ${config.alerting.configuredAlertCount} enabled`
            : undefined),
        ),
        channels.length > 0 && {
          title: 'Channels',
          rows: channels.map(channel => ({
            kind: 'bool',
            label: channel.name,
            value: channel.enabled,
            on: channel.defaultTarget ? `Enabled · ${channel.defaultTarget}` : 'Enabled',
            off: 'Disabled',
          })),
        },
      ].filter(Boolean).filter(g => g.rows.length > 0),
    })
  }

  /* Observability --------------------------------------------------------- */

  if (config.observability) {
    const otel = config.observability.openTelemetry || {}

    sections.push({
      id: 'observability',
      icon: 'monitoring',
      title: 'Observability',
      groups: [
        group('Seq',
          bool('Export', config.observability.seq?.enabled),
          code('Uri', config.observability.seq?.uri),
        ),
        group('OpenTelemetry',
          bool('Export', otel.enabled),
          code('Export path', otel.exportPath),
          code('Service', otel.service),
          code('Environment', otel.environment),
          code('Job', otel.job),
          code('Instance', otel.instance),
        ),
      ].filter(g => g.rows.length > 0),
    })
  }

  /* Infrastructure -------------------------------------------------------- */

  if (config.database) {
    sections.push({
      id: 'database',
      icon: 'database',
      title: 'Database',
      groups: [
        group(
          null,
          text('Provider', config.database.provider),
          code('Database', config.database.databaseName),
          code('Host', config.database.host),
        ),
      ],
    })
  }

  if (config.redis) {
    sections.push({
      id: 'redis',
      icon: 'bolt',
      title: 'Redis',
      note: 'Redis holds the live schedule. The key prefix is what separates one deployment '
        + 'from another when they share an instance.',
      groups: [
        group(
          null,
          code('Connection', config.redis.connectionString),
          text('Database', config.redis.database),
          code('Key prefix', config.redis.keyPrefix),
          text('Connect timeout', config.redis.connectTimeout, 'ms'),
          text('Sync timeout', config.redis.syncTimeout, 'ms'),
          text('Default lock TTL', config.redis.defaultLockTtlSeconds, 's'),
        ),
      ],
    })
  }

  if (config.rabbitMQ) {
    const mq = config.rabbitMQ
    const queues = mq.queues || {}

    sections.push({
      id: 'rabbitmq',
      icon: 'message',
      title: 'RabbitMQ',
      groups: [
        group('Connection',
          code('Host', mq.host),
          text('Port', mq.port),
          code('Virtual host', mq.virtualHost),
          text('Connection timeout', mq.connectionTimeout, 's'),
          text('Heartbeat', mq.heartbeat, 's'),
          bool('Automatic recovery', mq.automaticRecoveryEnabled),
          text('Network recovery interval', mq.networkRecoveryInterval, 's'),
        ),
        group('Topology',
          bool('Durable', mq.durable, 'Queues survive a broker restart.', 'Yes', 'No'),
          bool('Auto delete', mq.autoDelete, 'Queues are removed when the last consumer disconnects.', 'Yes', 'No'),
          code('Exchange', mq.exchange),
          code('Dead letter exchange', mq.deadLetterExchange),
        ),
        group('Depth thresholds',
          text('Warning at', mq.queueDepthWarningThreshold, ' messages'),
          text('Critical at', mq.queueDepthCriticalThreshold, ' messages'),
        ),
        group('Queues',
          code('Scheduled jobs', queues.scheduledJobs),
          code('Worker logs', queues.workerLogs),
          code('Worker heartbeat', queues.workerHeartbeat),
          code('Worker registration', queues.workerRegistration),
          code('Status updates', queues.statusUpdates),
          code('Failed occurrences', queues.failedOccurrences),
        ),
      ].filter(g => g.rows.length > 0),
    })
  }

  /* Runtime --------------------------------------------------------------- */

  sections.push({
    id: 'runtime',
    icon: 'computer',
    title: 'Runtime',
    groups: [
      group(
        null,
        text('Version', config.version),
        text('Environment', config.environment),
        code('Host', config.hostName),
        text('Started', config.startupTime ? new Date(config.startupTime).toLocaleString() : undefined),
        text('Uptime', formatUptime(config.uptime)),
        text('API key version', config.apiKeyVersion != null ? `v${config.apiKeyVersion}` : undefined),
      ),
    ],
  })

  return sections.filter(section => section.groups.length > 0)
}
