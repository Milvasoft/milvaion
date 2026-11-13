// Which SDK, and so which language, a worker is built with.
//
// Workers report it in their registration metadata as `sdk` / `sdkVersion`. Workers built with an
// SDK that predates those fields are recognised from their runtime version instead.

const LANGUAGES = {
  dotnet: { label: '.NET' },
  node: { label: 'Node.js' },
  go: { label: 'Go' },
  python: { label: 'Python' },
  java: { label: 'Java' },
  php: { label: 'PHP' },
  rust: { label: 'Rust' },
  ruby: { label: 'Ruby' },
}

const RUNTIME_PREFIXES = [
  ['Node.js', 'node'],
  ['Go ', 'go'],
  ['Python', 'python'],
  ['Java', 'java'],
  ['PHP', 'php'],
  ['Rust ', 'rust'],
  ['Ruby ', 'ruby'],
]

function field(metadata, camel, pascal) {
  return metadata?.[camel] ?? metadata?.[pascal] ?? null
}

function inferFromRuntime(runtime) {
  if (!runtime) return null

  const match = RUNTIME_PREFIXES.find(([prefix]) => runtime.startsWith(prefix))

  if (match) return match[1]

  // .NET workers report a bare version such as "10.0.1".
  return /^\d/.test(runtime) ? 'dotnet' : null
}

/**
 * Returns { key, label, sdkVersion, runtime } for a worker's metadata, or null when unknown.
 * `runtime` is ready to display, e.g. ".NET 10.0.1" or "Node.js v22.16.0".
 */
export function getWorkerLanguage(metadata) {
  const runtimeVersion = field(metadata, 'runtimeVersion', 'RuntimeVersion')
  const reported = field(metadata, 'sdk', 'Sdk')
  const key = (reported && LANGUAGES[reported.toLowerCase()] ? reported.toLowerCase() : null) ?? inferFromRuntime(runtimeVersion)

  if (!key) return null

  const runtime = key === 'dotnet' && runtimeVersion && /^\d/.test(runtimeVersion) ? `.NET ${runtimeVersion}` : runtimeVersion

  return {
    key,
    label: LANGUAGES[key].label,
    sdkVersion: field(metadata, 'sdkVersion', 'SdkVersion'),
    runtime,
  }
}
