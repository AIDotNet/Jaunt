// RegExp: global match, named groups, replace with callback, sticky, lookbehind, split, test loop.
function main() {
  let log = '';
  for (let i = 0; i < 1500; i++) log += `2024-0${i % 9 + 1}-${String(i % 28 + 1).padStart(2, '0')} 12:${String(i % 60).padStart(2, '0')}:00 [${['INFO', 'WARN', 'ERROR'][i % 3]}] user=u${i % 50} ip=10.0.${i % 255}.${(i * 7) % 255} msg="req ${i} took ${i % 900}ms"\n`;
  const re = /(?<date>\d{4}-\d{2}-\d{2}) (?<time>[\d:]+) \[(?<lvl>\w+)\] user=(?<user>\w+) ip=(?<ip>[\d.]+) msg="(?<msg>[^"]*)"/g;
  const counts = {};
  let m, ms = 0;
  while ((m = re.exec(log))) { counts[m.groups.lvl] = (counts[m.groups.lvl] || 0) + 1; ms += +/took (\d+)ms/.exec(m.groups.msg)[1]; }
  const masked = log.replace(/ip=(\d+)\.(\d+)\.(\d+)\.(\d+)/g, (_, a, b) => `ip=${a}.${b}.x.x`);
  const emails = 'a@b.co, bad@, x.y+z@ex-ample.org; q@r.io'.match(/[\w.+-]+@[\w-]+\.[a-z]{2,}/g);
  const words = log.split(/\s+/).length;
  const lb = (log.match(/(?<=user=)u\d+/g) || []).length;
  return [counts.INFO, counts.WARN, counts.ERROR, ms, masked.length, emails.length, words, lb].join(',');
}
