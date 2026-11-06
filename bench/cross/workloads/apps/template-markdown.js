// Text processing app: mini markdown -> HTML (regex replace with callbacks, line state machine) + mustache-like templating.
function md(src) {
  const out = []; let inList = false, inCode = false;
  const inline = t => t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>').replace(/\*([^*]+)\*/g, '<i>$1</i>').replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>');
  for (const line of src.split('\n')) {
    if (line.startsWith('```')) { out.push(inCode ? '</pre>' : '<pre>'); inCode = !inCode; continue; }
    if (inCode) { out.push(line.replace(/</g, '&lt;')); continue; }
    let m;
    if ((m = /^(#{1,6})\s+(.*)$/.exec(line))) { if (inList) { out.push('</ul>'); inList = false; } out.push(`<h${m[1].length}>${inline(m[2])}</h${m[1].length}>`); }
    else if ((m = /^[-*]\s+(.*)$/.exec(line))) { if (!inList) { out.push('<ul>'); inList = true; } out.push(`<li>${inline(m[1])}</li>`); }
    else { if (inList) { out.push('</ul>'); inList = false; } if (line.trim()) out.push(`<p>${inline(line)}</p>`); }
  }
  if (inList) out.push('</ul>'); return out.join('\n');
}
function tpl(t, ctx) { return t.replace(/\{\{#each (\w+)\}\}([\s\S]*?)\{\{\/each\}\}/g, (_, k, body) => ctx[k].map((it, i) => tpl(body, { ...ctx, ...it, '@i': i })).join('')).replace(/\{\{(@?[\w.]+)\}\}/g, (_, p) => String(p.split('.').reduce((o, k) => o?.[k], ctx) ?? '')); }
function main() {
  let doc = ''; for (let i = 0; i < 120; i++) doc += `# Title ${i}\nSome *emphasis* and **bold** with \`code\` and [link](http://x/${i}) <tag>.\n- item a${i}\n- item **b**\n\n\`\`\`\nlet x = ${i} < 3;\n\`\`\`\n`;
  const html = md(doc);
  const page = tpl('<ul>{{#each rows}}<li>{{@i}}:{{name}}={{n}}/{{user.id}}</li>{{/each}}</ul>', { rows: Array.from({ length: 500 }, (_, i) => ({ name: 'n' + i, n: i * 2 })), user: { id: 9 } });
  return [html.length, (html.match(/<li>/g) || []).length, page.length, page.slice(0, 40)].join(',');
}
