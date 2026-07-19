/**
 * Condition ifadelerinin ayrıştırılması ve üretilmesi.
 *
 * Dilbilgisi motordaki ConditionExpression.cs ile birebir aynı:
 *
 *   expression := term ( '||' term )*
 *   term       := factor ( '&&' factor )*
 *   factor     := '(' expression ')' | clause
 *
 * ve bir clause şu biçimde:
 *
 *   [stepId:](@status | $.field) <operatör> <değer>
 *
 * İki tarafın da aynı dilbilgisini uygulaması gerekiyor; ayrıştırıcı motordan
 * ayrılırsa editör ekranda bir şey gösterip motor başka bir şey çalıştırır.
 *
 * ── Ağaç biçimi ──────────────────────────────────────────────────────────────
 *
 * Bir grup, çocuklarını ve aralarındaki operatörleri ayrı ayrı tutuyor:
 *
 *   { type: 'group', children: [a, b, c], ops: ['&&', '||'], parenthesised: false }
 *
 * yani `a && b || c`. Tek bir operatör yerine bağlantı başına operatör tutmanın
 * sebebi, dilbilgisinin buna zaten izin vermesi: motor `A && B || C` ifadesini
 * sorunsuz değerlendiriyor ve öncelik kurallarına göre `(A && B) || C` diye
 * okuyor. Grubu tek operatöre bağlamak, motorun ifade edebildiğinden daha azını
 * kurdurmak olurdu.
 *
 * Öncelikten doğan iç içelik ayrıştırma sonrası düzleştiriliyor, böylece
 * `A && B || C` ekranda üç satır olarak görünüyor. Parantezden gelen gruplar
 * korunuyor - onlar önceliği bilerek değiştiriyor ve düzleştirilirlerse ifadenin
 * anlamı bozulurdu.
 */

const ANY_PARENT = ''

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

// ─── Ağaç düğümleri ──────────────────────────────────────────────────────────

export function makeRule() {
  return { type: 'rule', stepId: ANY_PARENT, kind: 'status', field: '', operator: '==', value: 'Completed' }
}

export function makeGroup(children = [], ops = [], parenthesised = true) {
  return { type: 'group', children, ops, parenthesised }
}

// ─── Sözcükleme ──────────────────────────────────────────────────────────────

/**
 * İfadeyi clause, operatör ve parantezlere ayırır.
 *
 * Tırnak farkındalığı var: karşılaştırılan bir değerin içindeki parantez ya da
 * operatör - `$.label == '(taslak)'` gibi - ifadeyi yeniden yapılandırmıyor.
 */
function tokenize(input) {
  const tokens = []
  let buffer = ''
  let quote = null

  const flush = () => {
    const text = buffer.trim()

    if (text) tokens.push({ type: 'clause', text })

    buffer = ''
  }

  for (let i = 0; i < input.length; i++) {
    const char = input[i]

    if (quote) {
      buffer += char

      if (char === quote) quote = null

      continue
    }

    if (char === "'" || char === '"') {
      quote = char
      buffer += char
      continue
    }

    if (char === '(') { flush(); tokens.push({ type: '(' }); continue }
    if (char === ')') { flush(); tokens.push({ type: ')' }); continue }

    if (char === '&' && input[i + 1] === '&') { flush(); tokens.push({ type: '&&' }); i++; continue }
    if (char === '|' && input[i + 1] === '|') { flush(); tokens.push({ type: '||' }); i++; continue }

    buffer += char
  }

  flush()

  return tokens
}

// ─── Clause ayrıştırma ───────────────────────────────────────────────────────

/**
 * Tek bir clause'u kural nesnesine çevirir, tanımadığı biçimde null döner.
 */
export function parseClause(raw) {
  let clause = raw.trim()
  let stepId = ANY_PARENT

  const colonIndex = clause.indexOf(':')

  if (colonIndex === 36 && GUID_PATTERN.test(clause.slice(0, colonIndex))) {
    stepId = clause.slice(0, colonIndex)
    clause = clause.slice(colonIndex + 1).trim()
  }

  if (clause.startsWith('@status')) {
    const rest = clause.slice(7).trim()
    const operator = rest.startsWith('!=') ? '!=' : '=='
    const value = rest.slice(operator.length).trim().replace(/^['"]|['"]$/g, '')

    if (!value) return null

    return { type: 'rule', stepId, kind: 'status', field: '', operator, value }
  }

  if (clause.startsWith('$.')) {
    const rest = clause.slice(2)

    // Uzun operatörler önce denenmeli, yoksa ">=" ">" olarak yakalanır.
    for (const operator of ['>=', '<=', '==', '!=', '>', '<']) {
      const index = rest.indexOf(` ${operator} `)

      if (index === -1) continue

      const field = rest.slice(0, index).trim()
      const value = rest.slice(index + operator.length + 2).trim().replace(/^['"]|['"]$/g, '')

      if (!field) return null

      return { type: 'rule', stepId, kind: 'field', field, operator, value }
    }
  }

  return null
}

// ─── Özyinelemeli inişli ayrıştırıcı ─────────────────────────────────────────

class Parser {
  constructor(tokens) {
    this.tokens = tokens
    this.position = 0
  }

  get done() {
    return this.position >= this.tokens.length
  }

  peek() {
    return this.tokens[this.position]?.type
  }

  parseExpression() {
    const children = [this.parseTerm()]
    const ops = []

    while (this.peek() === '||') {
      this.position++
      ops.push('||')
      children.push(this.parseTerm())
    }

    return children.length === 1 ? children[0] : makeGroup(children, ops, false)
  }

  parseTerm() {
    const children = [this.parseFactor()]
    const ops = []

    while (this.peek() === '&&') {
      this.position++
      ops.push('&&')
      children.push(this.parseFactor())
    }

    return children.length === 1 ? children[0] : makeGroup(children, ops, false)
  }

  parseFactor() {
    const token = this.tokens[this.position]

    if (!token) throw new Error('Unexpected end of expression')

    if (token.type === '(') {
      this.position++

      const inner = this.parseExpression()

      if (this.peek() !== ')') throw new Error('Unbalanced parenthesis')

      this.position++

      // Parantez bilerek konmuş, öncelikten doğmamış. İşaretlenmezse düzleştirme
      // onu açar ve `(A || B) && C` sessizce `A || B && C` olur.
      return inner.type === 'group'
        ? { ...inner, parenthesised: true }
        : inner
    }

    if (token.type !== 'clause') throw new Error(`Expected clause, found ${token.type}`)

    const rule = parseClause(token.text)

    if (!rule) throw new Error(`Unrecognised clause: ${token.text}`)

    this.position++

    return rule
  }
}

/**
 * Önceliğin ürettiği iç içeliği tek düzeye indirir.
 *
 * `A && B || C` ayrıştırıldığında iki katmanlı bir ağaç çıkıyor, ama ekranda üç
 * satır olarak görünmesi gerekiyor - kullanıcı onu öyle kurdu. Yalnızca
 * parantezsiz gruplar açılıyor; parantezli olanlar önceliği bilerek değiştirdiği
 * için oldukları gibi kalıyor.
 */
function flatten(node) {
  if (node.type === 'rule') return node

  const children = []
  const ops = []

  node.children.forEach((child, index) => {
    const flattened = flatten(child)

    if (flattened.type === 'group' && !flattened.parenthesised) {
      children.push(...flattened.children)
      ops.push(...flattened.ops)
    } else {
      children.push(flattened)
    }

    if (index < node.children.length - 1) ops.push(node.ops[index])
  })

  return { ...node, children, ops }
}

/**
 * İfadeyi düzenlenebilir bir ağaca çevirir. Kök her zaman bir grup, böylece editör
 * tek kural ile birden fazla kuralı aynı biçimde ele alıyor.
 *
 * Bir clause bile tanınmazsa null döner ve çağıran ham moda düşer; yarım anlaşılmış
 * bir ifadeyi görsel olarak göstermek, onu sessizce bozmaktan iyi değil.
 */
export function parseExpression(expression) {
  if (!expression?.trim()) return makeGroup([], [], false)

  try {
    const tokens = tokenize(expression)

    if (tokens.length === 0) return makeGroup([], [], false)

    const parser = new Parser(tokens)
    const node = parser.parseExpression()

    if (!parser.done) return null

    const root = node.type === 'group' ? node : makeGroup([node], [], false)

    return { ...flatten(root), parenthesised: false }
  } catch {
    return null
  }
}

// ─── Üretim ──────────────────────────────────────────────────────────────────

function buildClause(rule) {
  const prefix = rule.stepId ? `${rule.stepId}:` : ''

  if (rule.kind === 'status') {
    if (!rule.value) return null

    return `${prefix}@status ${rule.operator} '${rule.value}'`
  }

  if (!rule.field) return null

  // Sayılar tırnaksız, geri kalan her şey tırnaklı. Motor '>' ve '<' için sayısal
  // karşılaştırma deniyor ve tırnaklı bir sayı bunu bozar.
  const isNumeric = rule.value !== '' && !Number.isNaN(Number(rule.value))
  const value = isNumeric ? rule.value : `'${rule.value}'`

  return `${prefix}$.${rule.field} ${rule.operator} ${value}`
}

/**
 * Ağacı ifadeye çevirir.
 *
 * Üretilemeyen çocuklar - alanı doldurulmamış bir kural gibi - kendi bağlantı
 * operatörüyle birlikte düşüyor. Yalnızca çocuğu atmak, kalan operatörü yanlış
 * çifte kaydırırdı.
 */
export function buildExpression(node, isRoot = true) {
  if (!node) return ''

  if (node.type === 'rule') return buildClause(node) ?? ''

  const parts = []
  const ops = []

  node.children.forEach((child, index) => {
    const text = buildExpression(child, false)

    if (!text) return

    // İlk geçerli parça hariç, her parça kendinden önceki operatörle geliyor.
    if (parts.length > 0) ops.push(index > 0 ? node.ops[index - 1] : '&&')

    parts.push(text)
  })

  if (parts.length === 0) return ''

  let result = parts[0]

  for (let i = 1; i < parts.length; i++)
    result += ` ${ops[i - 1]} ${parts[i]}`

  return isRoot || parts.length === 1 ? result : `(${result})`
}

/**
 * Ağaçtaki kural sayısı. Editör boş durumu ayırt etmek için kullanıyor.
 */
export function countRules(node) {
  if (!node) return 0
  if (node.type === 'rule') return 1

  return node.children.reduce((sum, child) => sum + countRules(child), 0)
}

export { ANY_PARENT }
