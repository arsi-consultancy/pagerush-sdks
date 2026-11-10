package pagerush

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
)

// TemplatesService manages templates and renders them. Use it as
// client.Templates.
type TemplatesService struct {
	client *Client
}

// ListTemplatesParams are the optional parameters for Templates.List and
// Templates.ListAll.
type ListTemplatesParams struct {
	// Limit is the page size, 1 to 100. The API defaults to 50.
	Limit *int
	// Cursor is the NextCursor of the previous page.
	Cursor *string
}

// CreateTemplateParams is the body of Templates.Create. Send Source or
// Document, not both.
type CreateTemplateParams struct {
	// Name is required (1 to 120 characters).
	Name string `json:"name"`
	// Source is the template's main.typ.
	Source *string `json:"source,omitempty"`
	// Document is a template from the visual editor, as any JSON object.
	Document any `json:"document,omitempty"`
	// Files are other files the template imports or reads.
	Files []TemplateFile `json:"files,omitempty"`
	// SampleData is example data for dashboard test renders. Not used by API
	// renders.
	SampleData any `json:"sample_data,omitempty"`
	// TypstVersion is the Typst release to render with. The API picks its
	// current default when nil.
	TypstVersion *string `json:"typst_version,omitempty"`
}

// CreateVersionParams is the body of Templates.CreateVersion. Send Source or
// Document, not both.
type CreateVersionParams struct {
	Source       *string        `json:"source,omitempty"`
	Document     any            `json:"document,omitempty"`
	Files        []TemplateFile `json:"files,omitempty"`
	SampleData   any            `json:"sample_data,omitempty"`
	TypstVersion *string        `json:"typst_version,omitempty"`
	// Publish makes the new version live. The API defaults to true.
	Publish *bool `json:"publish,omitempty"`
}

func templatePath(id string, rest ...string) string {
	p := "/v1/templates/" + url.PathEscape(id)
	for _, r := range rest {
		p += "/" + r
	}
	return p
}

// templateEnvelope is the {"template": ...} wrapper of single-template
// responses.
type templateEnvelope struct {
	Template *Template `json:"template"`
}

func (e *templateEnvelope) valid() bool { return e.Template != nil }

// versionEnvelope is the {"version": ...} wrapper of GetVersion.
type versionEnvelope struct {
	Version *TemplateVersion `json:"version"`
}

func (e *versionEnvelope) valid() bool { return e.Version != nil }

func requireID(kind, id string) error {
	if id == "" {
		return clientError(CodeValidationFailed, "%s id is required", kind)
	}
	return nil
}

// List returns one page of the project's templates, newest first. params may
// be nil.
func (s *TemplatesService) List(ctx context.Context, params *ListTemplatesParams) (*TemplateList, error) {
	q := url.Values{}
	if params != nil {
		if params.Limit != nil {
			q.Set("limit", strconv.Itoa(*params.Limit))
		}
		if params.Cursor != nil {
			q.Set("cursor", *params.Cursor)
		}
	}
	var out TemplateList
	if err := s.client.getJSON(ctx, "/v1/templates", q, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAll returns an iterator over every template, fetching pages as needed.
// params may be nil; Limit sets the page size and Cursor a starting point.
//
//	it := client.Templates.ListAll(ctx, nil)
//	for it.Next() {
//		fmt.Println(it.Template().Name)
//	}
//	if err := it.Err(); err != nil {
//		log.Fatal(err)
//	}
func (s *TemplatesService) ListAll(ctx context.Context, params *ListTemplatesParams) *TemplateIterator {
	it := &TemplateIterator{ctx: ctx, svc: s}
	if params != nil {
		it.limit = params.Limit
		it.cursor = params.Cursor
	}
	return it
}

// TemplateIterator walks every template across pages. Call Next until it
// returns false, then check Err.
type TemplateIterator struct {
	ctx     context.Context
	svc     *TemplatesService
	limit   *int
	cursor  *string
	page    []Template
	pos     int
	current *Template
	started bool
	done    bool
	err     error
}

// Next advances to the next template, fetching the next page when needed. It
// returns false when there are no more templates or a request failed.
func (it *TemplateIterator) Next() bool {
	for {
		if it.err != nil {
			return false
		}
		if it.pos < len(it.page) {
			it.current = &it.page[it.pos]
			it.pos++
			return true
		}
		if it.done || (it.started && it.cursor == nil) {
			it.done = true
			it.current = nil
			return false
		}
		list, err := it.svc.List(it.ctx, &ListTemplatesParams{Limit: it.limit, Cursor: it.cursor})
		it.started = true
		if err != nil {
			it.err = err
			it.current = nil
			return false
		}
		it.page, it.pos, it.cursor = list.Items, 0, list.NextCursor
	}
}

// Template returns the current template. Only valid after Next returned true.
func (it *TemplateIterator) Template() *Template { return it.current }

// Err returns the error that stopped the iteration, if any.
func (it *TemplateIterator) Err() error { return it.err }

// Create creates a template. Its version 1 goes live.
func (s *TemplatesService) Create(ctx context.Context, params *CreateTemplateParams) (*TemplateWithVersion, error) {
	if params == nil {
		params = &CreateTemplateParams{}
	}
	var out TemplateWithVersion
	err := s.client.sendJSON(ctx, request{
		method: http.MethodPost,
		url:    s.client.apiURL("/v1/templates", nil),
		body:   params,
		policy: retryUnsafe,
	}, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

// Get returns a template with its version history (Template.Versions).
func (s *TemplatesService) Get(ctx context.Context, id string) (*Template, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	var out templateEnvelope
	if err := s.client.getJSON(ctx, templatePath(id), nil, &out); err != nil {
		return nil, err
	}
	return out.Template, nil
}

// Rename changes a template's name.
func (s *TemplatesService) Rename(ctx context.Context, id, name string) (*Template, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	var out templateEnvelope
	err := s.client.sendJSON(ctx, request{
		method: http.MethodPatch,
		url:    s.client.apiURL(templatePath(id), nil),
		body:   map[string]string{"name": name},
		policy: retryUnsafe,
	}, &out)
	if err != nil {
		return nil, err
	}
	return out.Template, nil
}

// Delete deletes a template and all its versions.
func (s *TemplatesService) Delete(ctx context.Context, id string) error {
	if err := requireID("template", id); err != nil {
		return err
	}
	return s.client.sendJSON(ctx, request{
		method: http.MethodDelete,
		url:    s.client.apiURL(templatePath(id), nil),
		policy: retrySafe,
	}, nil)
}

// CreateVersion adds a version to a template. It goes live unless
// params.Publish is false.
func (s *TemplatesService) CreateVersion(ctx context.Context, id string, params *CreateVersionParams) (*TemplateWithVersion, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	if params == nil {
		params = &CreateVersionParams{}
	}
	var out TemplateWithVersion
	err := s.client.sendJSON(ctx, request{
		method: http.MethodPost,
		url:    s.client.apiURL(templatePath(id, "versions"), nil),
		body:   params,
		policy: retryUnsafe,
	}, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

// GetVersion returns one version's source, files and sample data.
func (s *TemplatesService) GetVersion(ctx context.Context, id string, version int) (*TemplateVersion, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	var out versionEnvelope
	if err := s.client.getJSON(ctx, templatePath(id, "versions", strconv.Itoa(version)), nil, &out); err != nil {
		return nil, err
	}
	return out.Version, nil
}

// Publish makes a version the live one. Publishing an older version is how you
// roll back.
func (s *TemplatesService) Publish(ctx context.Context, id string, version int) (*Template, error) {
	if err := requireID("template", id); err != nil {
		return nil, err
	}
	var out templateEnvelope
	err := s.client.sendJSON(ctx, request{
		method: http.MethodPost,
		url:    s.client.apiURL(templatePath(id, "publish"), nil),
		body:   map[string]int{"version": version},
		policy: retryUnsafe,
	}, &out)
	if err != nil {
		return nil, err
	}
	return out.Template, nil
}
