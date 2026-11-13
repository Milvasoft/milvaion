package spec

import (
	"bytes"
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// Normalised values are: nil, string, int64, bool, []string, map[string]any
// (autoDisable) and, for KJSON, the canonical JSON text. Two values are equal
// exactly when their normalised forms are deeply equal.

// Normalize converts a value decoded from YAML (or JSON) into the field's
// normalised form, validating it on the way.
func Normalize(f *Field, v any) (any, error) {
	if v == nil {
		return nil, nil
	}
	switch f.Kind {
	case KString:
		switch s := v.(type) {
		case string:
			if s == "" {
				return nil, nil
			}
			return s, nil
		case int, int64, float64, bool:
			return fmt.Sprint(s), nil
		}
		return nil, fmt.Errorf("must be a string")
	case KInt:
		n, err := toInt(v)
		if err != nil {
			return nil, err
		}
		return n, nil
	case KBool:
		switch b := v.(type) {
		case bool:
			return b, nil
		case string:
			parsed, err := strconv.ParseBool(b)
			if err != nil {
				return nil, fmt.Errorf("must be true or false")
			}
			return parsed, nil
		}
		return nil, fmt.Errorf("must be true or false")
	case KTags, KList:
		var items []string
		switch t := v.(type) {
		case string:
			if f.Kind == KTags {
				items = client.SplitTags(t)
			} else {
				items = []string{t}
			}
		case []any:
			for _, item := range t {
				s, ok := item.(string)
				if !ok {
					s = fmt.Sprint(item)
				}
				if s = strings.TrimSpace(s); s != "" {
					items = append(items, s)
				}
			}
		case []string:
			items = append(items, t...)
		default:
			return nil, fmt.Errorf("must be a list of strings")
		}
		if f.Kind == KTags {
			items = uniqueSorted(items)
		}
		if len(items) == 0 {
			return nil, nil
		}
		return items, nil
	case KJSON:
		return canonicalJSON(v)
	case KEnum:
		s, ok := v.(string)
		if !ok {
			if n, err := toInt(v); err == nil && n >= 0 && int(n) < len(f.Enum) {
				return f.Enum[n], nil
			}
			return nil, fmt.Errorf("must be one of %s", strings.Join(f.Enum, ", "))
		}
		for _, e := range f.Enum {
			if strings.EqualFold(e, s) {
				return e, nil
			}
		}
		return nil, fmt.Errorf("invalid value %q, expected one of %s", s, strings.Join(f.Enum, ", "))
	case KTime:
		switch t := v.(type) {
		case time.Time:
			return t.UTC().Format(time.RFC3339), nil
		case string:
			if t == "" {
				return nil, nil
			}
			parsed, err := time.Parse(time.RFC3339, t)
			if err != nil {
				// The API may send a UTC time without a zone designator.
				parsed, err = time.Parse("2006-01-02T15:04:05.999999999", t)
			}
			if err != nil {
				return nil, fmt.Errorf("must be an RFC 3339 time such as 2026-01-31T09:00:00Z")
			}
			return parsed.UTC().Format(time.RFC3339), nil
		}
		return nil, fmt.Errorf("must be an RFC 3339 time such as 2026-01-31T09:00:00Z")
	case KAutoDisable:
		m, ok := v.(map[string]any)
		if !ok {
			return nil, fmt.Errorf("must be an object with enabled, threshold and failureWindowMinutes")
		}
		out := map[string]any{"enabled": true, "threshold": nil, "failureWindowMinutes": nil}
		for k, val := range m {
			switch k {
			case "enabled":
				b, ok := val.(bool)
				if !ok && val != nil {
					return nil, fmt.Errorf("enabled must be true or false")
				}
				if val != nil {
					out["enabled"] = b
				}
			case "threshold", "failureWindowMinutes":
				if val == nil {
					continue
				}
				n, err := toInt(val)
				if err != nil {
					return nil, fmt.Errorf("%s %w", k, err)
				}
				out[k] = n
			default:
				return nil, fmt.Errorf("unknown key %q (expected enabled, threshold, failureWindowMinutes)", k)
			}
		}
		return out, nil
	}
	return nil, fmt.Errorf("unsupported field kind")
}

// FromAPI reads the current value of a field from a job returned by the API.
func FromAPI(f *Field, job *client.Job) (any, error) {
	if f.Read != nil {
		return Normalize(f, f.Read(job))
	}
	raw, ok := job.Raw[f.Detail]
	if !ok || isJSONNull(raw) {
		return nil, nil
	}
	switch f.Kind {
	case KEnum:
		return Normalize(f, client.EnumName(f.Enum, raw))
	case KJSON:
		// The API stores the payload as a JSON string.
		var s string
		if err := json.Unmarshal(raw, &s); err == nil {
			if strings.TrimSpace(s) == "" {
				return nil, nil
			}
			var decoded any
			if err := json.Unmarshal([]byte(s), &decoded); err != nil {
				return s, nil
			}
			return canonicalJSON(decoded)
		}
		var decoded any
		_ = json.Unmarshal(raw, &decoded)
		return canonicalJSON(decoded)
	case KList:
		// Lists may arrive as a JSON array or as a JSON string holding one.
		var s string
		if err := json.Unmarshal(raw, &s); err == nil {
			raw = json.RawMessage(s)
		}
	}
	var decoded any
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	if err := dec.Decode(&decoded); err != nil {
		return nil, err
	}
	decoded = fromJSONNumbers(decoded)
	if f.Kind == KAutoDisable {
		// The API also returns bookkeeping (failure counts, disabledAt); only the settings are compared.
		m, _ := decoded.(map[string]any)
		settings := map[string]any{}
		for _, k := range []string{"enabled", "threshold", "failureWindowMinutes"} {
			if m != nil {
				settings[k] = m[k]
			}
		}
		decoded = settings
	}
	return Normalize(f, decoded)
}

// ToAPI converts a normalised value into what the create and update commands expect.
func ToAPI(f *Field, v any) any {
	if v == nil {
		return nil
	}
	switch f.Kind {
	case KTags:
		return strings.Join(v.([]string), ",")
	case KJSON:
		return v.(string)
	case KEnum:
		for i, e := range f.Enum {
			if e == v.(string) {
				return i
			}
		}
	}
	return v
}

// ToYAMLValue converts a normalised value into a value that reads well in YAML.
func ToYAMLValue(f *Field, v any) any {
	if v == nil {
		return nil
	}
	if f.Kind == KJSON {
		var decoded any
		if err := json.Unmarshal([]byte(v.(string)), &decoded); err != nil {
			return v
		}
		return fromJSONNumbers(decoded)
	}
	return v
}

// Format prints a normalised value on one line for diff output.
func Format(f *Field, v any) string {
	if f != nil && f.Kind == KJSON && v != nil {
		return v.(string)
	}
	switch t := v.(type) {
	case nil:
		return "(none)"
	case string:
		return strconv.Quote(t)
	case []string:
		return "[" + strings.Join(t, ", ") + "]"
	case map[string]any:
		b, _ := json.Marshal(t)
		return string(b)
	}
	return fmt.Sprint(v)
}

func toInt(v any) (int64, error) {
	switch n := v.(type) {
	case int:
		return int64(n), nil
	case int64:
		return n, nil
	case uint64:
		return int64(n), nil
	case float64:
		if n != math.Trunc(n) {
			return 0, fmt.Errorf("must be a whole number")
		}
		return int64(n), nil
	case json.Number:
		return n.Int64()
	case string:
		parsed, err := strconv.ParseInt(n, 10, 64)
		if err != nil {
			return 0, fmt.Errorf("must be a whole number")
		}
		return parsed, nil
	}
	return 0, fmt.Errorf("must be a whole number")
}

func uniqueSorted(items []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, s := range items {
		if !seen[s] {
			seen[s] = true
			out = append(out, s)
		}
	}
	sort.Strings(out)
	return out
}

// canonicalJSON returns the JSON text of v with sorted keys, or nil for an empty object.
func canonicalJSON(v any) (any, error) {
	if s, ok := v.(string); ok {
		// A string is accepted as JSON text so existing payloads can be pasted as-is.
		trimmed := strings.TrimSpace(s)
		if trimmed == "" {
			return nil, nil
		}
		var decoded any
		if err := json.Unmarshal([]byte(trimmed), &decoded); err != nil {
			return nil, fmt.Errorf("is a string but not valid JSON: %v", err)
		}
		v = decoded
	}
	v = yamlToJSONCompatible(v)
	if m, ok := v.(map[string]any); ok && len(m) == 0 {
		return nil, nil
	}
	b, err := json.Marshal(v)
	if err != nil {
		return nil, err
	}
	return string(b), nil
}

// yamlToJSONCompatible converts map[any]any (which encoding/json cannot marshal) recursively.
func yamlToJSONCompatible(v any) any {
	switch t := v.(type) {
	case map[string]any:
		out := make(map[string]any, len(t))
		for k, val := range t {
			out[k] = yamlToJSONCompatible(val)
		}
		return out
	case map[any]any:
		out := make(map[string]any, len(t))
		for k, val := range t {
			out[fmt.Sprint(k)] = yamlToJSONCompatible(val)
		}
		return out
	case []any:
		out := make([]any, len(t))
		for i, val := range t {
			out[i] = yamlToJSONCompatible(val)
		}
		return out
	case time.Time:
		return t.UTC().Format(time.RFC3339Nano)
	}
	return v
}

// fromJSONNumbers turns json.Number and integral float64 values into int64 so
// they print as integers in YAML.
func fromJSONNumbers(v any) any {
	switch t := v.(type) {
	case map[string]any:
		for k, val := range t {
			t[k] = fromJSONNumbers(val)
		}
		return t
	case []any:
		for i, val := range t {
			t[i] = fromJSONNumbers(val)
		}
		return t
	case json.Number:
		if n, err := t.Int64(); err == nil {
			return n
		}
		f, _ := t.Float64()
		return f
	case float64:
		if t == math.Trunc(t) && math.Abs(t) < 1<<53 {
			return int64(t)
		}
	}
	return v
}

func isJSONNull(raw json.RawMessage) bool {
	s := strings.TrimSpace(string(raw))
	return s == "" || s == "null"
}
