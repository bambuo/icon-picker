package main

import (
	"bufio"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type job struct {
	code string
	hall string
	name string
	icon string
}

// safeFile keeps a game code usable as a single path segment.
func safeFile(code string) string {
	var b strings.Builder
	for _, r := range code {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '_', r == '-', r == '.':
			b.WriteRune(r)
		default:
			b.WriteByte('_')
		}
	}
	s := strings.Trim(b.String(), ".")
	if s == "" || strings.Contains(s, "..") {
		return ""
	}
	return s
}

func loadJobs(listPath string) ([]job, error) {
	f, err := os.Open(listPath)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var jobs []job
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if len(parts) < 4 {
			return nil, fmt.Errorf("bad line: %q", line)
		}
		jobs = append(jobs, job{code: parts[0], hall: parts[1], name: parts[2], icon: parts[3]})
	}
	return jobs, sc.Err()
}

func download(client *http.Client, base, outDir string, j job) (int, error) {
	dst := filepath.Join(outDir, j.code+".jpg")
	if st, err := os.Stat(dst); err == nil && st.Size() > 1024 {
		return 0, nil // already downloaded
	}
	var lastErr error
	for attempt := 1; attempt <= 3; attempt++ {
		req, err := http.NewRequest("GET", base+j.icon, nil)
		if err != nil {
			return 0, err
		}
		resp, err := client.Do(req)
		if err != nil {
			lastErr = err
			time.Sleep(time.Duration(attempt) * time.Second)
			continue
		}
		if resp.StatusCode != http.StatusOK {
			resp.Body.Close()
			lastErr = fmt.Errorf("http %d", resp.StatusCode)
			if resp.StatusCode >= 500 {
				time.Sleep(time.Duration(attempt) * time.Second)
				continue
			}
			return resp.StatusCode, lastErr
		}
		tmp, err := os.CreateTemp(outDir, ".dl-*")
		if err != nil {
			resp.Body.Close()
			return 0, err
		}
		_, err = io.Copy(tmp, resp.Body)
		resp.Body.Close()
		if cerr := tmp.Close(); err == nil {
			err = cerr
		}
		if err != nil {
			os.Remove(tmp.Name())
			return 0, err
		}
		if err := os.Rename(tmp.Name(), dst); err != nil {
			os.Remove(tmp.Name())
			return 0, err
		}
		return resp.StatusCode, nil
	}
	return 0, lastErr
}

func main() {
	list := flag.String("list", "/tmp/real-icons.tsv", "TSV: code\\thall\\tname\\ticonPath")
	out := flag.String("out", "真实图标", "output directory")
	base := flag.String("base", "https://statictest.image-oss.online", "image host prefix")
	conc := flag.Int("c", 8, "concurrency")
	flag.Parse()

	jobs, err := loadJobs(*list)
	if err != nil {
		fmt.Fprintln(os.Stderr, "load list:", err)
		os.Exit(1)
	}
	if err := os.MkdirAll(*out, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, "mkdir:", err)
		os.Exit(1)
	}

	client := &http.Client{Timeout: 30 * time.Second}
	ch := make(chan job)
	var ok, skipped, failed int64
	var mu sync.Mutex
	var fails []string
	var wg sync.WaitGroup

	for i := 0; i < *conc; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := range ch {
				status, err := download(client, *base, *out, j)
				switch {
				case err != nil:
					atomic.AddInt64(&failed, 1)
					mu.Lock()
					fails = append(fails, fmt.Sprintf("%s (%v)", j.code, err))
					mu.Unlock()
				case status == 0:
					atomic.AddInt64(&skipped, 1)
				default:
					n := atomic.AddInt64(&ok, 1)
					if n%25 == 0 {
						fmt.Printf("  已下载 %d/%d\n", n, len(jobs))
					}
				}
			}
		}()
	}
	start := time.Now()
	for _, j := range jobs {
		ch <- j
	}
	close(ch)
	wg.Wait()

	fmt.Printf("完成：共 %d，下载 %d，跳过(已存在) %d，失败 %d，耗时 %.1fs\n", len(jobs), ok, skipped, failed, time.Since(start).Seconds())
	for _, f := range fails {
		fmt.Println("  FAIL", f)
	}
}
