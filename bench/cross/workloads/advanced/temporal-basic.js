// Temporal (ES2026-track): PlainDate/PlainDateTime/Duration arithmetic and comparison. Reported as ERR if unsupported.
function main() {
  let d = Temporal.PlainDate.from('2024-01-31'), t = 0;
  for (let i = 0; i < 600; i++) {
    d = d.add({ days: 3, months: i % 2 });
    t += d.dayOfWeek + d.weekOfYear + d.daysInMonth + (d.inLeapYear ? 1 : 0);
    const dur = Temporal.Duration.from({ hours: i, minutes: i * 7 });
    t += dur.total('minutes') % 13 + Temporal.PlainDate.compare(d, Temporal.PlainDate.from('2025-06-15'));
  }
  const dt = Temporal.PlainDateTime.from('2024-03-10T08:30:00').add({ hours: 40 });
  return [d.toString(), t, dt.toString(), Temporal.PlainDate.from('2024-02-29').until('2025-03-01').toString()].join(',');
}
