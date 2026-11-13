package spec

import (
	"fmt"
	"io"
	"reflect"
	"sort"
	"strings"

	"github.com/Milvasoft/milvaion/tools/milvaion-cli/pkg/client"
)

// Action is what apply does to one job.
type Action int

const (
	// NoChange means the job already matches the file.
	NoChange Action = iota
	// Create adds a job that does not exist yet.
	Create
	// Update changes fields in place.
	Update
	// Replace deletes the job and creates it again, because a field cannot be changed in place.
	Replace
	// Delete removes a managed job that is no longer in the file (only with --prune).
	Delete
)

func (a Action) String() string {
	return [...]string{"no change", "create", "update", "replace", "delete"}[a]
}

func (a Action) symbol() string {
	return [...]string{" ", "+", "~", "-/+", "-"}[a]
}

// Change is one field that differs.
type Change struct {
	Field    *Field
	Old, New any
}

// Step is the plan for one job.
type Step struct {
	Action  Action
	Name    string
	ID      string
	Desired *Job
	Changes []Change
	// AddsManagedTag is set when an existing job not created by apply is adopted.
	AddsManagedTag bool
}

// Current is a job that exists on the server.
type Current struct {
	ID      string
	Spec    Job
	Managed bool
}

// Plan is the ordered list of steps.
type Plan struct {
	Steps []Step
}

// HasChanges reports whether applying the plan would change anything.
func (p *Plan) HasChanges() bool {
	for _, s := range p.Steps {
		if s.Action != NoChange {
			return true
		}
	}
	return false
}

// Count returns how many steps have the action.
func (p *Plan) Count(a Action) int {
	n := 0
	for _, s := range p.Steps {
		if s.Action == a {
			n++
		}
	}
	return n
}

// ComputePlan compares the desired jobs with the current ones (keyed by name).
// managed lists every job that carries the managed tag; with prune, those that
// are not in desired are deleted.
func ComputePlan(desired []Job, current map[string]Current, managed []Current, prune bool) Plan {
	var plan Plan
	inFile := map[string]bool{}
	for i := range desired {
		d := &desired[i]
		inFile[d.Name] = true
		cur, exists := current[d.Name]
		if !exists {
			plan.Steps = append(plan.Steps, Step{Action: Create, Name: d.Name, Desired: d})
			continue
		}
		step := Step{Action: NoChange, Name: d.Name, ID: cur.ID, Desired: d, AddsManagedTag: !cur.Managed}
		step.Changes = Compare(d, &cur.Spec)
		for _, c := range step.Changes {
			if c.Field.Update == "" {
				step.Action = Replace
				break
			}
			step.Action = Update
		}
		if step.Action == NoChange && step.AddsManagedTag {
			step.Action = Update
		}
		plan.Steps = append(plan.Steps, step)
	}
	if prune {
		var deletes []Step
		for _, m := range managed {
			if !inFile[m.Spec.Name] {
				deletes = append(deletes, Step{Action: Delete, Name: m.Spec.Name, ID: m.ID})
			}
		}
		sort.Slice(deletes, func(i, j int) bool { return deletes[i].Name < deletes[j].Name })
		plan.Steps = append(plan.Steps, deletes...)
	}
	return plan
}

// Compare returns the managed fields whose desired value differs from the current one.
func Compare(desired, current *Job) []Change {
	var changes []Change
	for i := range Fields {
		f := &Fields[i]
		want, managed := desired.Value(f)
		if !managed {
			continue
		}
		if f.Skip != nil && f.Skip(effective(desired)) {
			continue
		}
		have := current.Values[f.Name]
		if have == nil {
			have = f.Default
		}
		if !reflect.DeepEqual(want, have) {
			changes = append(changes, Change{Field: f, Old: have, New: want})
		}
	}
	return changes
}

func effective(j *Job) map[string]any {
	out := map[string]any{}
	for i := range Fields {
		if v, ok := j.Value(&Fields[i]); ok && v != nil {
			out[Fields[i].Name] = v
		}
	}
	return out
}

// CreateBody builds the CreateScheduledJobCommand for a desired job.
func CreateBody(j *Job, managedTag string) map[string]any {
	body := map[string]any{"displayName": j.Name}
	for i := range Fields {
		f := &Fields[i]
		if f.Create == "" {
			continue
		}
		v, ok := j.Value(f)
		if !ok || v == nil {
			continue
		}
		if f.Kind == KTags {
			continue
		}
		body[f.Create] = ToAPI(f, v)
	}
	body["tags"] = tagString(j, managedTag)
	return body
}

// UpdateBody builds the changed properties of an UpdateScheduledJobCommand.
func UpdateBody(step *Step, managedTag string) map[string]any {
	changes := map[string]any{}
	for _, c := range step.Changes {
		if c.Field.Update == "" {
			continue
		}
		if c.Field.Kind == KTags {
			continue
		}
		changes[c.Field.Update] = ToAPI(c.Field, c.New)
	}
	tagsChanged := false
	for _, c := range step.Changes {
		if c.Field.Kind == KTags {
			tagsChanged = true
		}
	}
	if tagsChanged || (step.AddsManagedTag && managedTag != "") {
		changes["tags"] = tagString(step.Desired, managedTag)
	}
	return changes
}

func tagString(j *Job, managedTag string) string {
	f, _ := FieldByName("tags")
	var tags []string
	if v, ok := j.Value(f); ok && v != nil {
		tags = append(tags, v.([]string)...)
	}
	if managedTag != "" {
		tags = append(tags, managedTag)
	}
	return strings.Join(uniqueSorted(tags), ",")
}

// Print writes a human readable plan.
func (p *Plan) Print(w io.Writer, verbose bool) {
	for _, s := range p.Steps {
		if s.Action == NoChange && !verbose {
			continue
		}
		label := s.Name
		if s.ID != "" {
			label += " (" + s.ID + ")"
		}
		fmt.Fprintf(w, "%s %s %s\n", s.Action.symbol(), s.Action, label)
		switch s.Action {
		case Create:
			for i := range Fields {
				f := &Fields[i]
				if v, ok := s.Desired.Value(f); ok && v != nil {
					if f.Skip != nil && f.Skip(effective(s.Desired)) {
						continue
					}
					fmt.Fprintf(w, "      %s: %s\n", f.Name, Format(f, v))
				}
			}
		case Update, Replace:
			for _, c := range s.Changes {
				note := ""
				if c.Field.Update == "" {
					note = "   # forces replacement"
				}
				fmt.Fprintf(w, "      %s: %s -> %s%s\n", c.Field.Name, Format(c.Field, c.Old), Format(c.Field, c.New), note)
			}
			if s.AddsManagedTag {
				fmt.Fprintf(w, "      (adopts an existing job: adds the managed tag)\n")
			}
		}
	}
	fmt.Fprintf(w, "\nPlan: %d to create, %d to update, %d to replace, %d to delete, %d unchanged.\n",
		p.Count(Create), p.Count(Update), p.Count(Replace), p.Count(Delete), p.Count(NoChange))
}

// ManagedTagOf reports whether a server job's tag string includes the managed tag.
func ManagedTagOf(tags, managedTag string) bool {
	if managedTag == "" {
		return false
	}
	for _, t := range client.SplitTags(tags) {
		if t == managedTag {
			return true
		}
	}
	return false
}
