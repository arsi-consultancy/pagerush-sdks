package pagerush

import (
	"encoding/json"
	"time"
)

// Template is a stored Typst template. Field names follow the API.
type Template struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	// LiveVersion is the version renders use unless they ask for another.
	// Nil until a version is published.
	LiveVersion   *int      `json:"live_version"`
	LatestVersion int       `json:"latest_version"`
	CreatedAt     time.Time `json:"created_at"`
	UpdatedAt     time.Time `json:"updated_at"`
	// Versions is the version history, newest first. Only Templates.Get
	// fills it.
	Versions []TemplateVersionSummary `json:"versions,omitempty"`
}

// TemplateVersionSummary describes one version in Template.Versions.
type TemplateVersionSummary struct {
	Version       int       `json:"version"`
	Live          bool      `json:"live"`
	Bytes         int64     `json:"bytes"`
	FileCount     int       `json:"file_count"`
	HasSampleData bool      `json:"has_sample_data"`
	TypstVersion  string    `json:"typst_version"`
	CreatedAt     time.Time `json:"created_at"`
}

// TemplateVersion is one immutable version of a template: its main.typ
// source, other files and sample data.
type TemplateVersion struct {
	Version int    `json:"version"`
	Source  string `json:"source"`
	// Document is the visual-editor document the version was made from, or
	// JSON null.
	Document json.RawMessage `json:"document,omitempty"`
	Files    []TemplateFile  `json:"files"`
	// SampleData is the example data for dashboard test renders, or JSON null.
	SampleData   json.RawMessage `json:"sample_data,omitempty"`
	TypstVersion string          `json:"typst_version"`
	CreatedAt    time.Time       `json:"created_at"`
}

// TemplateWithVersion is returned when a template or version is created.
type TemplateWithVersion struct {
	Template *Template        `json:"template"`
	Version  *TemplateVersion `json:"version"`
}

func (t *TemplateWithVersion) valid() bool { return t.Template != nil && t.Version != nil }

// TemplateList is one page of templates, newest first.
type TemplateList struct {
	Items []Template `json:"items"`
	// NextCursor is passed as ListTemplatesParams.Cursor for the next page.
	// Nil on the last page.
	NextCursor *string `json:"next_cursor"`
}

// Job statuses.
const (
	JobStatusQueued    = "queued"
	JobStatusRendering = "rendering"
	JobStatusSucceeded = "succeeded"
	JobStatusFailed    = "failed"
)

// Job is a render job.
type Job struct {
	ID string `json:"id"`
	// Status is one of the JobStatus constants.
	Status string `json:"status"`
	// Mode is "sync" or "async".
	Mode string `json:"mode"`
	// Region is where the job rendered and its PDF is stored.
	Region string `json:"region"`
	// TemplateID is nil once the template has been deleted.
	TemplateID      *string           `json:"template_id"`
	TemplateVersion *int              `json:"template_version"`
	Engine          *string           `json:"engine"`
	Bytes           *int64            `json:"bytes"`
	DurationMS      *int64            `json:"duration_ms"`
	Error           *JobError         `json:"error"`
	Delivery        *JobDelivery      `json:"delivery"`
	Metadata        map[string]string `json:"metadata"`
	// DownloadURL is a signed link to the PDF, valid for up to an hour. Nil
	// once the file has expired or was deleted after download.
	DownloadURL         *string    `json:"download_url"`
	DeleteAfterDownload bool       `json:"delete_after_download"`
	CreatedAt           time.Time  `json:"created_at"`
	CompletedAt         *time.Time `json:"completed_at"`
}

func (j *Job) valid() bool { return j.ID != "" }

// Done reports whether the job has finished (succeeded or failed).
func (j *Job) Done() bool {
	return j.Status == JobStatusSucceeded || j.Status == JobStatusFailed
}

// JobError is why a job failed.
type JobError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

// JobDelivery is the state of callback_url delivery for a job.
type JobDelivery struct {
	// Status is "pending", "delivered" or "failed".
	Status   string  `json:"status"`
	Attempts int     `json:"attempts"`
	Error    *string `json:"error"`
}
