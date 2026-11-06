// moejs side of the cross-engine benchmark. Same protocol as bench/Jaunt.Benchmarks/CrossBench.cs:
// compile (median) -> cold (new runtime + load + first call, verified) -> warm-up -> samples of k calls.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/MoeForge/moejs"
)

type rec map[string]any

type runner struct {
	rt       *moejs.Runtime
	main     moejs.Value
	result   moejs.Value
	hasRes   bool
}

func newRunner(s *moejs.Script) (*runner, error) {
	rt := moejs.NewRuntime(moejs.Options{})
	if _, err := rt.RunScript(s); err != nil {
		return nil, err
	}
	return &runner{rt: rt}, nil
}

// Globals are looked up by running a tiny script that evaluates to the function.
func (r *runner) bind(getMain, getResult *moejs.Script) error {
	m, err := r.rt.RunScript(getMain)
	if err != nil {
		return err
	}
	r.main = m
	if v, err := r.rt.RunScript(getResult); err == nil {
		r.result, r.hasRes = v, !v.IsUndefined()
	}
	return nil
}

func (r *runner) call() (string, error) {
	v, err := r.rt.CallFunction(r.main, moejs.Undefined())
	if err != nil {
		return "", err
	}
	if r.hasRes {
		if v, err = r.rt.CallFunction(r.result, moejs.Undefined()); err != nil {
			return "", err
		}
	}
	g, err := r.rt.ToGo(v)
	if err != nil {
		return "", err
	}
	return fmt.Sprint(g), nil
}

func medianUs(f func()) float64 {
	f()
	var xs []float64
	start := time.Now()
	for len(xs) < 5 || (len(xs) < 200 && time.Since(start) < 300*time.Millisecond) {
		t := time.Now()
		f()
		xs = append(xs, float64(time.Since(t).Nanoseconds())/1000)
	}
	sort.Float64s(xs)
	return xs[len(xs)/2]
}

func measure(name, src string, expected string, hasExpected bool, targetMs float64, samples int, warmMs float64) (out rec) {
	out = rec{"engine": "moejs", "name": name}
	defer func() {
		if x := recover(); x != nil {
			out["status"], out["error"] = "error", fmt.Sprint("panic: ", x)
		}
	}()
	script, err := moejs.CompileScript(name, src)
	if err != nil {
		out["status"], out["error"] = "error", firstLine(err.Error())
		return
	}
	out["compileUs"] = medianUs(func() { _, _ = moejs.CompileScript(name, src) })
	getMain, _ := moejs.CompileScript("m", "main;")
	getResult, _ := moejs.CompileScript("r", "typeof result === 'function' ? result : undefined;")

	t0 := time.Now()
	r, err := newRunner(script)
	if err == nil {
		err = r.bind(getMain, getResult)
	}
	var first string
	if err == nil {
		first, err = r.call()
	}
	if err != nil {
		out["status"], out["error"] = "error", firstLine(err.Error())
		return
	}
	out["coldUs"] = float64(time.Since(t0).Nanoseconds()) / 1000
	out["result"] = first
	ok := !hasExpected || first == expected
	out["ok"] = ok

	w, start := 0, time.Now()
	for w < 3 || float64(time.Since(start).Milliseconds()) < warmMs {
		if _, err := r.call(); err != nil {
			out["status"], out["error"] = "error", firstLine(err.Error())
			return
		}
		w++
	}
	est := float64(time.Since(start).Nanoseconds()) / 1e6 / float64(w)
	k := int(targetMs / maxf(est, 0.001))
	if k < 1 {
		k = 1
	}
	per := make([]float64, samples)
	var alloc uint64
	var ms0, ms1 runtime.MemStats
	for i := 0; i < samples; i++ {
		runtime.GC()
		runtime.ReadMemStats(&ms0)
		t := time.Now()
		for j := 0; j < k; j++ {
			_, _ = r.call()
		}
		per[i] = float64(time.Since(t).Nanoseconds()) / 1000 / float64(k)
		runtime.ReadMemStats(&ms1)
		alloc += ms1.TotalAlloc - ms0.TotalAlloc
	}
	sort.Float64s(per)
	out["medianUs"], out["minUs"] = per[samples/2], per[0]
	out["allocB"], out["inner"] = int64(alloc)/(int64(samples)*int64(k)), k
	if ok {
		out["status"] = "ok"
	} else {
		out["status"] = "mismatch"
	}
	return
}

func maxf(a, b float64) float64 {
	if a > b {
		return a
	}
	return b
}

func firstLine(s string) string { return strings.SplitN(s, "\n", 2)[0] }

func main() {
	dir := flag.String("dir", "", "workloads dir")
	expPath := flag.String("expected", "", "expected.json")
	filter := flag.String("filter", "", "substring filter")
	targetMs := flag.Float64("target-ms", 60, "sample duration")
	samples := flag.Int("samples", 7, "samples")
	warmMs := flag.Float64("warm-ms", 150, "warm-up ms")
	outPath := flag.String("json", "", "jsonl output")
	flag.Parse()
	expected := map[string]string{}
	if b, err := os.ReadFile(*expPath); err == nil {
		_ = json.Unmarshal(b, &expected)
	}
	var files []string
	_ = filepath.WalkDir(*dir, func(p string, d os.DirEntry, err error) error {
		if err == nil && !d.IsDir() && strings.HasSuffix(p, ".js") {
			files = append(files, p)
		}
		return nil
	})
	sort.Strings(files)
	var w *os.File
	if *outPath != "" {
		w, _ = os.Create(*outPath)
		defer w.Close()
	}
	for _, f := range files {
		rel, _ := filepath.Rel(*dir, f)
		name := strings.TrimSuffix(filepath.ToSlash(rel), ".js")
		if *filter != "" && !strings.Contains(name, *filter) {
			continue
		}
		src, _ := os.ReadFile(f)
		exp, has := expected[name]
		r := measure(name, string(src), exp, has, *targetMs, *samples, *warmMs)
		b, _ := json.Marshal(r)
		if w != nil {
			fmt.Fprintln(w, string(b))
		}
		med := ""
		if m, ok := r["medianUs"].(float64); ok {
			med = fmt.Sprintf("%.1f µs", m)
		}
		fmt.Printf("moejs  %-34s %-9v %s\n", name, r["status"], med)
	}
}
