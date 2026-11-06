// Redux-style immutable updates: spread, array copy, nested update helpers, selectors, memoization, middleware chain.
function reducer(s, a) {
  switch (a.type) {
    case 'add': return { ...s, items: [...s.items, { id: a.id, text: a.text, done: false }], count: s.count + 1 };
    case 'toggle': return { ...s, items: s.items.map(i => i.id === a.id ? { ...i, done: !i.done } : i) };
    case 'remove': return { ...s, items: s.items.filter(i => i.id !== a.id), count: s.count - 1 };
    case 'rename': return { ...s, meta: { ...s.meta, history: [...s.meta.history.slice(-9), a.text] } };
    default: return s;
  }
}
const logger = []; const mw = store => next => action => { logger.push(action.type); return next(action); };
function createStore(r, init, ...mws) { let s = init; const subs = []; let dispatch = a => { s = r(s, a); subs.forEach(f => f(s)); return a; }; const api = { getState: () => s }; dispatch = mws.reduceRight((n, m) => m(api)(n), dispatch); return { dispatch, getState: () => s, subscribe: f => subs.push(f) }; }
function createSelector(f, g) { let lastIn, lastOut; return s => { const i = f(s); if (i !== lastIn) { lastIn = i; lastOut = g(i); } return lastOut; }; }
function main() {
  logger.length = 0;
  const store = createStore(reducer, { items: [], count: 0, meta: { history: [] } }, mw, mw);
  const doneCount = createSelector(s => s.items, items => items.filter(i => i.done).length);
  let notified = 0, sel = 0; store.subscribe(s => { notified++; sel += doneCount(s); });
  for (let i = 0; i < 1500; i++) { store.dispatch({ type: 'add', id: i, text: 't' + i }); if (i % 3 === 0) store.dispatch({ type: 'toggle', id: i }); if (i % 7 === 0) store.dispatch({ type: 'remove', id: i - 1 }); if (i % 50 === 0) store.dispatch({ type: 'rename', text: 'r' + i }); }
  const s = store.getState();
  return [s.items.length, s.count, notified, sel, logger.length, s.meta.history.join('')].join(',');
}
