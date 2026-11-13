package spec

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"reflect"
	"sort"
	"strings"

	"gopkg.in/yaml.v3"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// APIVersion is the schema version this build reads and writes.
const APIVersion = "milvaion/v1"

// Document kinds.
const (
	KindJob     = "Job"
	KindJobList = "JobList"
)

// Job is one job in the desired state.
type Job struct {
	// Name is the job's display name. It is the key apply matches on, so it must be unique.
	Name string
	// Values holds the normalised value of every field that was set in the file.
	Values map[string]any
	// Source says where the job was defined, for error messages.
	Source string
}

// Value returns the value apply will enforce for f: the one in the file, or the
// field's default. ok is false when the field is not managed for this job.
func (j *Job) Value(f *Field) (v any, ok bool) {
	if v, set := j.Values[f.Name]; set {
		return v, true
	}
	if f.OmitMeansUnmanaged {
		return nil, false
	}
	return f.Default, true
}

type rawDocument struct {
	APIVersion string         `yaml:"apiVersion"`
	Kind       string         `yaml:"kind"`
	Metadata   rawMetadata    `yaml:"metadata"`
	Spec       map[string]any `yaml:"spec"`
	Items      []yaml.Node    `yaml:"items"`
}

type rawMetadata struct {
	Name string `yaml:"name"`
}

// Parse reads one or more YAML documents. Each is a `kind: Job` or a
// `kind: JobList` whose items are jobs. source names the input in errors.
func Parse(r io.Reader, source string) ([]Job, error) {
	dec := yaml.NewDecoder(r)
	var jobs []Job
	for i := 1; ; i++ {
		var node yaml.Node
		err := dec.Decode(&node)
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, fmt.Errorf("%s: %w", source, err)
		}
		if isEmptyDocument(&node) {
			continue
		}
		parsed, err := parseNode(&node, fmt.Sprintf("%s (document %d)", source, i), true)
		if err != nil {
			return nil, err
		}
		jobs = append(jobs, parsed...)
	}
	if err := checkUnique(jobs); err != nil {
		return nil, err
	}
	return jobs, nil
}

func isEmptyDocument(n *yaml.Node) bool {
	if n.Kind == yaml.DocumentNode {
		return len(n.Content) == 0 || isEmptyDocument(n.Content[0])
	}
	return n.Kind == 0 || (n.Kind == yaml.ScalarNode && n.Tag == "!!null")
}

func parseNode(node *yaml.Node, where string, allowList bool) ([]Job, error) {
	var doc rawDocument
	if err := node.Decode(&doc); err != nil {
		return nil, fmt.Errorf("%s: %w", where, err)
	}
	if err := checkKeys(node, where, []string{"apiVersion", "kind", "metadata", "spec", "items"}); err != nil {
		return nil, err
	}
	if doc.APIVersion != APIVersion {
		if doc.APIVersion == "" {
			return nil, fmt.Errorf("%s: apiVersion is missing; expected %s", where, APIVersion)
		}
		return nil, fmt.Errorf("%s: unsupported apiVersion %q; this milvaion build reads %s", where, doc.APIVersion, APIVersion)
	}
	switch doc.Kind {
	case KindJob:
		job, err := parseJob(doc, where)
		if err != nil {
			return nil, err
		}
		return []Job{job}, nil
	case KindJobList, "List":
		if !allowList {
			return nil, fmt.Errorf("%s: a %s cannot be nested inside another list", where, doc.Kind)
		}
		var jobs []Job
		for i := range doc.Items {
			item := &doc.Items[i]
			itemWhere := fmt.Sprintf("%s, item %d", where, i+1)
			// Items may omit apiVersion and kind; they inherit the list's.
			if !hasKey(item, "apiVersion") || !hasKey(item, "kind") {
				var m map[string]any
				if err := item.Decode(&m); err != nil {
					return nil, fmt.Errorf("%s: %w", itemWhere, err)
				}
				if _, ok := m["apiVersion"]; !ok {
					m["apiVersion"] = doc.APIVersion
				}
				if _, ok := m["kind"]; !ok {
					m["kind"] = KindJob
				}
				var rebuilt yaml.Node
				if err := rebuilt.Encode(m); err != nil {
					return nil, err
				}
				item = &rebuilt
			}
			parsed, err := parseNode(item, itemWhere, false)
			if err != nil {
				return nil, err
			}
			jobs = append(jobs, parsed...)
		}
		return jobs, nil
	case "":
		return nil, fmt.Errorf("%s: kind is missing; expected %s or %s", where, KindJob, KindJobList)
	}
	return nil, fmt.Errorf("%s: unsupported kind %q; expected %s or %s", where, doc.Kind, KindJob, KindJobList)
}

func parseJob(doc rawDocument, where string) (Job, error) {
	name := strings.TrimSpace(doc.Metadata.Name)
	if name == "" {
		return Job{}, fmt.Errorf("%s: metadata.name is required", where)
	}
	where = fmt.Sprintf("%s, job %q", where, name)
	job := Job{Name: name, Values: map[string]any{}, Source: where}

	keys := make([]string, 0, len(doc.Spec))
	for k := range doc.Spec {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		f, ok := FieldByName(k)
		if !ok {
			return Job{}, fmt.Errorf("%s: unknown field spec.%s%s", where, k, suggest(k))
		}
		v, err := Normalize(f, doc.Spec[k])
		if err != nil {
			return Job{}, fmt.Errorf("%s: spec.%s %v", where, k, err)
		}
		if v != nil {
			job.Values[k] = v
		}
	}
	if err := validate(&job); err != nil {
		return Job{}, fmt.Errorf("%s: %w", where, err)
	}
	return job, nil
}

func validate(j *Job) error {
	external := j.Values["external"] == true
	if external {
		if j.Values["externalJobId"] == nil {
			return errors.New("spec.externalJobId is required when spec.external is true")
		}
		return nil
	}
	cron, hasCron := j.Values["cron"].(string)
	if !hasCron && j.Values["executeAt"] == nil {
		return errors.New("set spec.cron for a recurring job or spec.executeAt for a one-time job")
	}
	if hasCron {
		if n := len(strings.Fields(cron)); n != 6 {
			return fmt.Errorf("spec.cron %q has %d fields; Milvaion cron expressions have six, seconds first (\"0 0 2 * * *\" is 02:00 daily)", cron, n)
		}
	}
	return nil
}

func checkUnique(jobs []Job) error {
	seen := map[string]string{}
	for _, j := range jobs {
		if prev, dup := seen[j.Name]; dup {
			return fmt.Errorf("job %q is defined twice: %s and %s", j.Name, prev, j.Source)
		}
		seen[j.Name] = j.Source
	}
	return nil
}

func checkKeys(node *yaml.Node, where string, allowed []string) error {
	m := node
	if m.Kind == yaml.DocumentNode && len(m.Content) > 0 {
		m = m.Content[0]
	}
	if m.Kind != yaml.MappingNode {
		return fmt.Errorf("%s: expected a mapping with apiVersion, kind, metadata and spec", where)
	}
	for i := 0; i < len(m.Content); i += 2 {
		key := m.Content[i].Value
		found := false
		for _, a := range allowed {
			if a == key {
				found = true
			}
		}
		if !found {
			return fmt.Errorf("%s: unknown top-level key %q (line %d)", where, key, m.Content[i].Line)
		}
	}
	return nil
}

func hasKey(node *yaml.Node, key string) bool {
	m := node
	if m.Kind == yaml.DocumentNode && len(m.Content) > 0 {
		m = m.Content[0]
	}
	for i := 0; i+1 < len(m.Content); i += 2 {
		if m.Content[i].Value == key {
			return true
		}
	}
	return false
}

func suggest(name string) string {
	lower := strings.ToLower(name)
	best, bestDist := "", 3
	for _, f := range Fields {
		if d := editDistance(lower, strings.ToLower(f.Name)); d < bestDist {
			best, bestDist = f.Name, d
		}
	}
	if best != "" {
		return fmt.Sprintf(" (did you mean %s?)", best)
	}
	names := make([]string, len(Fields))
	for i, f := range Fields {
		names[i] = f.Name
	}
	return " (known fields: " + strings.Join(names, ", ") + ")"
}

func editDistance(a, b string) int {
	prev := make([]int, len(b)+1)
	for j := range prev {
		prev[j] = j
	}
	for i := 1; i <= len(a); i++ {
		cur := make([]int, len(b)+1)
		cur[0] = i
		for j := 1; j <= len(b); j++ {
			cost := 1
			if a[i-1] == b[j-1] {
				cost = 0
			}
			cur[j] = min(prev[j]+1, cur[j-1]+1, prev[j-1]+cost)
		}
		prev = cur
	}
	return prev[len(b)]
}

// FromAPIJob builds the spec of an existing job. managedTag, when set, is removed
// from the tags because it is bookkeeping, not part of the job's definition.
func FromAPIJob(job *client.Job, managedTag string) (Job, error) {
	out := Job{Name: job.DisplayName, Values: map[string]any{}, Source: "server job " + job.ID}
	for i := range Fields {
		f := &Fields[i]
		v, err := FromAPI(f, job)
		if err != nil {
			return Job{}, fmt.Errorf("job %q: reading %s: %w", job.DisplayName, f.Name, err)
		}
		if f.Kind == KTags && managedTag != "" && v != nil {
			v = withoutTag(v.([]string), managedTag)
		}
		if v != nil {
			out.Values[f.Name] = v
		}
	}
	return out, nil
}

func withoutTag(tags []string, tag string) any {
	var out []string
	for _, t := range tags {
		if t != tag {
			out = append(out, t)
		}
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

// Encode writes jobs as YAML. One job is written as a `kind: Job` document;
// several as a single `kind: JobList`.
func Encode(w io.Writer, jobs []Job) error {
	var root *yaml.Node
	if len(jobs) == 1 {
		root = jobNode(jobs[0], true)
	} else {
		items := &yaml.Node{Kind: yaml.SequenceNode}
		for _, j := range jobs {
			items.Content = append(items.Content, jobNode(j, false))
		}
		root = mapping(
			"apiVersion", scalar(APIVersion),
			"kind", scalar(KindJobList),
			"items", items,
		)
	}
	var buf bytes.Buffer
	enc := yaml.NewEncoder(&buf)
	enc.SetIndent(2)
	if err := enc.Encode(root); err != nil {
		return err
	}
	if err := enc.Close(); err != nil {
		return err
	}
	_, err := w.Write(buf.Bytes())
	return err
}

func jobNode(j Job, withHeader bool) *yaml.Node {
	specNode := &yaml.Node{Kind: yaml.MappingNode}
	for i := range Fields {
		f := &Fields[i]
		v, ok := j.Values[f.Name]
		if !ok || v == nil {
			continue
		}
		// Do not write values that equal the default; it keeps exported files short.
		if f.Default != nil && reflect.DeepEqual(f.Default, v) {
			continue
		}
		if f.Skip != nil && f.Skip(j.Values) {
			continue
		}
		var valueNode yaml.Node
		if err := valueNode.Encode(ToYAMLValue(f, v)); err != nil {
			valueNode = *scalar(fmt.Sprint(v))
		}
		specNode.Content = append(specNode.Content, scalar(f.Name), &valueNode)
	}
	var pairs []any
	if withHeader {
		pairs = append(pairs, "apiVersion", scalar(APIVersion), "kind", scalar(KindJob))
	}
	pairs = append(pairs, "metadata", mapping("name", scalar(j.Name)), "spec", specNode)
	return mapping(pairs...)
}

func scalar(s string) *yaml.Node {
	return &yaml.Node{Kind: yaml.ScalarNode, Value: s, Tag: "!!str"}
}

func mapping(pairs ...any) *yaml.Node {
	n := &yaml.Node{Kind: yaml.MappingNode}
	for i := 0; i < len(pairs); i += 2 {
		n.Content = append(n.Content, scalar(pairs[i].(string)), pairs[i+1].(*yaml.Node))
	}
	return n
}
