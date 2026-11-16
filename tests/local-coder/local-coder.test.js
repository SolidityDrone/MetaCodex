'use strict'
// The local coder's answers through the three APIs the agent harnesses speak (docs/local-coder.md):
//   OpenCode     POST /v1/chat/completions   (OpenCode's tools: bash, read, write, ...)
//   Claude Code  POST /v1/messages           (Claude Code's tools: Bash, Read, Write, ...)
//   Codex        POST /v1/responses          (Codex's tools: exec_command, write_stdin, ...)
// For each model output below (the forms Qwen2.5-Coder 0.5B writes), streamed and not: the text the user sees and the
// tool calls the harness gets. A call never reaches the user as JSON text; prose stays prose.
// Run: tests/local-coder/run-local-coder-tests.sh   (node --test, no install)
const test = require('node:test')
const assert = require('node:assert/strict')
const h = require('./harness')

const NOTE = h.toolcalls.BROKEN_CALL_NOTE
const HARNESSES = {
  opencode: h.chatApi,
  claude: h.messagesApi,
  codex: h.responsesApi
}

// the call each harness should get
const shell = (who, cmd) => who === 'codex' ? { name: 'exec_command', args: { cmd } } : who === 'claude' ? { name: 'Bash', args: { command: cmd }, strict: true } : { name: 'bash', args: { command: cmd, description: cmd } }
const write = (who, file, content) => who === 'codex' ? { name: 'exec_command', args: { cmd: `mkdir -p -- "$(dirname -- '${file}')" && printf %s '${content}' > '${file}'` } }
  : who === 'claude' ? { name: 'Write', args: { file_path: file, content }, strict: true } : { name: 'write', args: { filePath: file, content } }
const read = (who, file) => who === 'codex' ? { name: 'exec_command', args: { cmd: `cat -- '${file}'` } } : who === 'claude' ? { name: 'Read', args: { file_path: file }, strict: true } : { name: 'read', args: { filePath: file } }
const none = () => null

const LS = '{"name": "bash", "arguments": {"command": "ls -la"}}'
const CASES = [
  { name: 'tagged <tool_call> (the template\'s own format)', out: `<tool_call>\n${LS}\n</tool_call>`, text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'fenced ```json block', out: '```json\n' + LS + '\n```', text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'bare JSON object', out: LS, text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'bare JSON, capitalised name, "parameters"', out: '{"name": "Bash", "parameters": {"command": "ls -la"}}', text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'the owner\'s case: a question answered with an echo call', out: '{"name": "bash", "arguments": {"command": "echo hello world"}}', text: 'hello world', call: none },
  { name: 'prose answer', out: 'A closure is a function that keeps the variables of the scope where it was made.', text: 'A closure is a function that keeps the variables of the scope where it was made.', call: none },
  {
    name: 'prose with a JSON example in a code block (not a call)',
    out: 'A minimal package.json looks like this:\n```json\n{"name": "demo", "version": "1.0.0", "scripts": {"start": "node index.js"}}\n```\nThen run npm start.',
    text: 'A minimal package.json looks like this:\n```json\n{"name": "demo", "version": "1.0.0", "scripts": {"start": "node index.js"}}\n```\nThen run npm start.',
    call: none
  },
  {
    name: 'prose with a call-shaped example naming no tool of the request',
    out: 'To ask for the weather a model writes {"name": "get_weather", "arguments": {"city": "Paris"}} and waits for the result.',
    text: 'To ask for the weather a model writes {"name": "get_weather", "arguments": {"city": "Paris"}} and waits for the result.',
    call: none
  },
  {
    name: 'broken JSON (stray quote, from the 2026-11-12 Codex run)',
    out: '{"name": "exec_command", "arguments": {"cmd": "python3 hello.py", "justification": "Need to run hello.py", "login": true, "max_output_tokens": 10000"}}',
    text: '',
    call: (w) => shell(w, 'python3 hello.py')
  },
  { name: 'call cut off by the output limit', out: '{"name": "write", "arguments": {"path": "hello.py", "content": "print(\\"hel', text: NOTE, call: none },
  {
    name: 'a lead-in sentence and a fenced write call',
    out: 'I\'ll create the file.\n```json\n{"name": "write", "arguments": {"path": "hello.py", "content": "print(\\"hello world\\")\\n"}}\n```',
    text: 'I\'ll create the file.',
    call: (w) => write(w, 'hello.py', 'print("hello world")\n')
  },
  { name: 'raw line break inside a JSON string', out: '{"name": "write", "arguments": {"path": "notes.txt", "content": "line one\nline two"}}', text: '', call: (w) => write(w, 'notes.txt', 'line one\nline two') },
  { name: 'empty think block, then a tagged call', out: `<think>\n\n</think>\n\n<tool_call>\n${LS}\n</tool_call>`, text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'a made-up tool response after the call', out: `<tool_call>\n${LS}\n</tool_call>\n<tool_response>\nhello.py\n</tool_response>\nThe folder has hello.py.`, text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'OpenAI-style function object, arguments as a string', out: '{"type": "function", "function": {"name": "read_file", "arguments": "{\\"path\\": \\"hello.py\\"}"}}', text: '', call: (w) => read(w, 'hello.py') },
  { name: 'a code answer in a python block', out: 'Here is hello world in Python:\n```python\nprint("hello world")\n```', text: 'Here is hello world in Python:\n```python\nprint("hello world")\n```', call: none },
  { name: 'two calls, the second made up', out: LS + '\n{"name": "hello.txt", "arguments": {"content": "hi"}}', text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'Python-style single quotes', out: "{'name': 'bash', 'arguments': {'command': 'ls -la'}}", text: '', call: (w) => shell(w, 'ls -la') },
  { name: 'the whole answer is a call to a tool the slim list left out', out: '{"name": "edit", "arguments": {"path": "a.py", "old": "x", "new": "y"}}', text: '', call: () => ({ name: 'edit', args: { path: 'a.py', old: 'x', new: 'y' } }) },
  { name: 'an echo with two quoted words stays a call', out: '{"name": "bash", "arguments": {"command": "echo \\"a\\" \\"b\\""}}', text: '', call: (w) => shell(w, 'echo "a" "b"') },
  { name: 'a fenced block inside the tags', out: '<tool_call>\n```json\n' + LS + '\n```\n</tool_call>', text: '', call: (w) => shell(w, 'ls -la') }
]

function checkCall (actual, want, label) {
  assert.equal(actual.name, want.name, label + ': tool name')
  for (const [k, v] of Object.entries(want.args)) assert.deepEqual(actual.args[k], v, label + ': argument ' + k)
  if (want.strict) assert.deepEqual(Object.keys(actual.args).sort(), Object.keys(want.args).sort(), label + ': no argument the strict schema does not declare')
}

for (const c of CASES) {
  for (const [who, api] of Object.entries(HARNESSES)) {
    for (const stream of [false, true]) {
      test(`${who} ${stream ? 'stream' : 'json'}: ${c.name}`, async () => {
        const r = await api(c.out, { stream })
        const label = `${who}/${stream ? 'stream' : 'json'}`
        assert.equal(r.text, c.text, label + ': user-visible text')
        const want = c.call(who)
        assert.equal(r.calls.length, want ? 1 : 0, label + ': number of calls ' + JSON.stringify(r.calls))
        if (want) checkCall(r.calls[0], want, label)
        if (r.finish !== undefined) assert.equal(r.finish, want ? (who === 'claude' ? 'tool_use' : 'tool_calls') : (who === 'claude' ? 'end_turn' : 'stop'), label + ': finish reason')
        if (stream) {
          // nothing streamed before the parse that the final text does not have (a call is never streamed as text)
          assert.equal(r.deltas.join(''), c.text, label + ': streamed deltas')
          if (!c.text.includes('"name"')) for (const d of r.deltas) assert.ok(!/"name"|<tool_call>/.test(d), label + ': a delta carries call text: ' + d)
          if (r.final) { assert.equal(r.final.text, c.text, label + ': response.completed text'); assert.equal(r.final.calls.length, r.calls.length) }
        }
      })
    }
  }
}

// ---------------------------------------------------------------- the prompt the small model gets

test('Claude Code: slim system prompt, the request\'s own tool names, required arguments only, temperature capped', async () => {
  const r = await h.messagesApi('A closure keeps its scope.')
  const { prompt, runOpts } = r.engines.calls[0]
  const tools = prompt.filter((p) => p.type === 'function')
  assert.deepEqual(tools.map((t) => t.name), ['Bash', 'Read', 'Write'])
  assert.deepEqual(Object.keys(tools[0].parameters.properties), ['command'])
  assert.deepEqual(Object.keys(tools[2].parameters.properties), ['file_path', 'content'])
  const sys = prompt.find((p) => p.role === 'system').content
  assert.match(sys, /If the user asks a question or just talks, answer in plain text and call no tool\./)
  assert.match(sys, /- Bash: runs a shell command/)
  assert.match(sys, /<env>[\s\S]*Working directory: \/root\/demo[\s\S]*<\/env>/)
  assert.match(sys, /Instructions from: \/root\/demo\/AGENTS\.md\nBuild with make\./)
  assert.ok(!sys.includes('Follow the style rules'), 'the harness\'s own long prompt is replaced')
  assert.ok(sys.length < 2600, 'system prompt is short: ' + sys.length)
  assert.equal(runOpts.generationParams.temp, 0.2, 'Claude Code\'s temperature 1 is capped')
})

test('OpenCode: argument order path before content, the required description kept', async () => {
  const r = await h.chatApi('ok')
  const tools = r.engines.calls[0].prompt.filter((p) => p.type === 'function')
  assert.deepEqual(tools.map((t) => t.name), ['bash', 'read', 'write'])
  assert.deepEqual(Object.keys(tools[0].parameters.properties), ['command', 'description'])
  assert.deepEqual(Object.keys(tools[2].parameters.properties), ['filePath', 'content'])
  assert.equal(r.engines.calls[0].runOpts.generationParams.temp, 0, 'no temperature asked: greedy')
})

test('Codex: only its shell tool goes to the model', async () => {
  const r = await h.responsesApi('ok')
  assert.deepEqual(r.engines.calls[0].prompt.filter((p) => p.type === 'function').map((t) => t.name), ['exec_command'])
})

test('a request with no tools (a title) keeps its short system prompt', async () => {
  const r = await h.chatApi('Closures in JavaScript', { tools: [], system: 'You are a title generator. Output only a title.' })
  assert.equal(r.engines.calls[0].prompt.find((p) => p.role === 'system').content, 'You are a title generator. Output only a title.')
  assert.equal(r.text, 'Closures in JavaScript')
})

test('no tools: a call-shaped JSON answer stays text (there is no tool to call)', async () => {
  const r = await h.chatApi(LS, { tools: [], system: 'Answer in JSON.' })
  assert.equal(r.text, LS)
  assert.equal(r.calls.length, 0)
})

// ---------------------------------------------------------------- the pieces

test('parseLoose repairs', () => {
  const p = h.toolcalls.parseLoose
  assert.deepEqual(p('{"a": 1, }').value, { a: 1 })
  assert.deepEqual(p('{"a": {"b": 2}').value, { a: { b: 2 } })
  assert.deepEqual(p('{"a": 10000"}').value, { a: 10000 })
  assert.deepEqual(p('{"a": "x\ny"}').value, { a: 'x\ny' })
  assert.deepEqual(p('{"a": 1}}').value, { a: 1 })
  assert.equal(p('{"a": "unfinished').cut, true)
  assert.equal(p('not json').value, null)
})

test('callGuard: prose streams as it comes, from a brace or a fence on it is held', () => {
  const got = []
  const g = h.toolcalls.callGuard((t) => got.push(t))
  for (const t of ['Use the ', '`', 'ls', '` command', '. Then ', '{', '"x": 1}']) g.feed(t)
  assert.equal(got.join(''), 'Use the `ls` command. Then') // trailing white space waits for what follows it
  assert.equal(g.rest('Use the `ls` command. Then {"x": 1}'), ' {"x": 1}')
  assert.equal(g.rest('Use the `ls` command. Then'), '')
})

test('the interface LLM (not the coder) parses as before: exact tool names, no aliases', () => {
  const tools = ['open_panel']
  const r1 = h.toolcalls.parseAssistant('{"name": "open_panel", "arguments": {"name": "voice"}}', tools)
  assert.deepEqual(r1.tool_calls, [{ name: 'open_panel', arguments: { name: 'voice' } }])
  assert.equal(r1.content, '')
  const r2 = h.toolcalls.parseAssistant('{"name": "Open_Panel", "arguments": {}}', tools)
  assert.equal(r2.tool_calls.length, 0)
  const r3 = h.toolcalls.parseAssistant('<tool_call>\n{"name": "open_panel", "arguments": {"name": "voice"}}\n</tool_call>', tools)
  assert.deepEqual(r3.tool_calls, [{ name: 'open_panel', arguments: { name: 'voice' } }])
  const r4 = h.toolcalls.parseAssistant('{"name": "bash", "arguments": {"command": "echo hi"}}', ['bash'])
  assert.equal(r4.tool_calls.length, 1, 'no echo-as-text outside the coder')
})
