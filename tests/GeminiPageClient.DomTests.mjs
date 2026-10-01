// Offline DOM regression for the production expression; no browser or Gemini account.
// Run: node --test tests/GeminiPageClient.DomTests.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import test from 'node:test';

const require = createRequire(new URL('../src/AgentLocalWeb.Web/package.json', import.meta.url));
const { JSDOM } = require('jsdom');
const source = readFileSync(new URL('../src/AgentLocalWeb.Brain.GeminiWeb/GeminiPageClient.cs', import.meta.url), 'utf8');
const method = source.slice(source.indexOf('private async Task<bool> PromptMatchesAsync('));
const expression = method.match(/var expression = \$\$"""([\s\S]*?)""";/)[1];
const responseMethod = source.slice(source.indexOf('private async Task<IReadOnlyList<string>> ReadNewResponsesAsync('));
const responseExpression = responseMethod.match(/var expression = \$\$"""([\s\S]*?)""";/)[1];

function matches(expected, lines, configure = () => {}) {
  const dom = new JSDOM('<rich-textarea><div class="ql-editor" contenteditable="true"></div></rich-textarea>', { runScripts: 'outside-only' });
  try {
    const editor = dom.window.document.querySelector('.ql-editor');
    for (const line of lines) {
      const paragraph = dom.window.document.createElement('p');
      if (line === '') paragraph.append(dom.window.document.createElement('br'));
      else paragraph.textContent = line;
      editor.append(paragraph);
    }
    // Reproduce the live browser's visual paragraph separators. jsdom has no layout.
    Object.defineProperty(editor, 'innerText', { get: () => lines.map(line => line || '\n').join('\n\n') });
    editor.getBoundingClientRect = () => ({ width: 500, height: 168 });
    configure(editor, dom.window.document);
    return dom.window.eval(expression.replace('{{expected}}', () => JSON.stringify(expected.replace(/\r\n/g, '\n'))));
  } finally {
    dom.window.close();
  }
}

test('verifies a multiline Windows prompt despite visual paragraph separators', () => {
  assert.equal(matches('first\r\n\r\nsecond\r\n', ['first', '', 'second', '']), true);
});

test('preserves multiple intentional blank lines and inline formatting', () => {
  assert.equal(matches('first\n\n\nsecond', ['first', '', '', 'second'], (editor, document) => {
    const strong = document.createElement('strong');
    strong.textContent = 'first';
    editor.firstChild.replaceChildren(strong);
  }), true);
});

test('reads an inline break as a newline and an empty paragraph as one empty line', () => {
  assert.equal(matches('first\nsecond\n', ['firstsecond', ''], (editor, document) => {
    editor.firstChild.replaceChildren('first', document.createElement('br'), 'second');
  }), true);
});

test('rejects altered text, missing lines and extra blank lines', () => {
  assert.equal(matches('first\nsecond', ['first', 'changed']), false);
  assert.equal(matches('first\n\nsecond', ['first', 'second']), false);
  assert.equal(matches('first\nsecond', ['first', '', 'second']), false);
});

test('does not interpret unsupported editor structure as Quill paragraphs', () => {
  assert.equal(matches('first\nsecond', ['first', 'second'], editor => editor.classList.remove('ql-editor')), false);
  assert.equal(matches('first\nsecond', ['first', 'second'], (editor, document) => {
    editor.append(document.createTextNode('extra'));
  }), false);
});

test('retains existing single-line and textarea verification', () => {
  assert.equal(matches('first', ['first']), true);
  assert.equal(matches('first\nsecond', [], (editor, document) => {
    const textarea = document.createElement('textarea');
    textarea.setAttribute('role', 'textbox');
    textarea.value = 'first\nsecond';
    textarea.getBoundingClientRect = () => ({ width: 500, height: 168 });
    editor.replaceWith(textarea);
  }), true);
});

test('reads response textContent without rendered line-wrap artifacts', () => {
  const dom = new JSDOM('<div class="model-response-text"></div>', { runScripts: 'outside-only' });
  try {
    const response = dom.window.document.querySelector('.model-response-text');
    response.textContent = '{"protocol":"local-agent/v1","type":"final","message":"long answer"}';
    Object.defineProperty(response, 'innerText', {
      get: () => '{"protocol":"local-agent/v1","type":"final",\n"message":"long answer"}',
    });
    const values = dom.window.eval(
      responseExpression.replace('{{marker}}', () => JSON.stringify('baseline')),
    );
    assert.equal(values.length, 1);
    assert.equal(values[0], response.textContent);
    assert.doesNotThrow(() => JSON.parse(values[0]));
  } finally {
    dom.window.close();
  }
});
