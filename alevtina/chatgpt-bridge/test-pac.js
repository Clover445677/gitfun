const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = process.argv[2];
const context = {};
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(root, 'openai-route.pac'), 'utf8'), context);
for (const [host, proxy] of [
  ['chatgpt.com', true], ['AUTH.OPENAI.COM.', true], ['files.oaiusercontent.com', true],
  ['chatgpt.com.evil.invalid', false], ['notopenai.com', false], ['youtube.com', false],
  ['127.0.0.1', false], ['example.com', false],
]) {
  const value = context.FindProxyForURL(`https://${host}/`, host);
  const expected = proxy ? 'PROXY 127.0.0.1:18881' : 'DIRECT';
  if (value !== expected) throw new Error(`${host}: ${value}, expected ${expected}`);
}
fs.appendFileSync(path.join(root, 'test-report.txt'), 'PASS Chrome PAC domain boundaries and no DIRECT fallback for OpenAI.\n');
console.log('PASS Chrome PAC');
