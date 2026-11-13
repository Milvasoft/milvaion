// Package client is a small HTTP client for the Milvaion REST API.
//
// It authenticates with an api key sent in the X-ApiKey header and unwraps the
// response envelope ({isSuccess, statusCode, messages, data}) that every Milvaion
// endpoint returns. The CLI and the Terraform provider both use it.
package client

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// DefaultUserAgent identifies the client in API access logs.
const DefaultUserAgent = "milvaion-cli"

// Client talks to one Milvaion API.
type Client struct {
	// BaseURL is the Milvaion address, including any base path, e.g. https://milvaion.example.com.
	BaseURL string
	// APIKey is sent in the X-ApiKey header.
	APIKey string
	// UserAgent is sent with every request.
	UserAgent string
	// HTTP is the underlying client. A client with a 60 second timeout is used when nil.
	HTTP *http.Client
}

// New returns a client for the given address and api key.
func New(baseURL, apiKey string) *Client {
	return &Client{
		BaseURL:   strings.TrimRight(baseURL, "/"),
		APIKey:    apiKey,
		UserAgent: DefaultUserAgent,
		HTTP:      &http.Client{Timeout: 60 * time.Second},
	}
}

// Message is one entry of the envelope's messages list.
type Message struct {
	Key     string `json:"key"`
	Message string `json:"message"`
	Type    any    `json:"type"`
}

type envelope struct {
	IsSuccess      bool            `json:"isSuccess"`
	StatusCode     int             `json:"statusCode"`
	Messages       []Message       `json:"messages"`
	Data           json.RawMessage `json:"data"`
	TotalDataCount *int            `json:"totalDataCount"`
	NextCursor     *string         `json:"nextCursor"`
	HasNextPage    *bool           `json:"hasNextPage"`
}

// APIError is returned when the API rejects a request, either with a non-2xx
// status or with isSuccess false in the envelope.
type APIError struct {
	HTTPStatus int
	Messages   []string
	Body       string
}

func (e *APIError) Error() string {
	msg := strings.Join(e.Messages, "; ")
	if msg == "" {
		msg = strings.TrimSpace(e.Body)
	}
	if len(msg) > 500 {
		msg = msg[:500] + "..."
	}
	switch e.HTTPStatus {
	case http.StatusUnauthorized:
		return "unauthorized (401): the api key is missing, invalid, expired or revoked"
	case http.StatusForbidden:
		return "forbidden (403): the api key lacks the permission this command needs" + suffix(msg)
	}
	if e.HTTPStatus >= 300 || e.HTTPStatus < 200 {
		return fmt.Sprintf("milvaion api returned HTTP %d%s", e.HTTPStatus, suffix(msg))
	}
	if msg == "" {
		msg = "request failed"
	}
	return "milvaion api: " + msg
}

func suffix(s string) string {
	if s == "" {
		return ""
	}
	return ": " + s
}

// IsNotFound reports whether err means the requested record does not exist.
func IsNotFound(err error) bool {
	return errors.Is(err, ErrNotFound)
}

// ErrNotFound is returned by Get* methods when the record does not exist.
var ErrNotFound = errors.New("not found")

func (c *Client) do(ctx context.Context, method, path string, query url.Values, body any) (*envelope, error) {
	if c.BaseURL == "" {
		return nil, errors.New("no Milvaion URL configured: run `milvaion login` or set MILVAION_URL")
	}
	u := c.BaseURL + "/api/v1" + path
	if len(query) > 0 {
		u += "?" + query.Encode()
	}

	var reader io.Reader
	if body != nil {
		buf, err := json.Marshal(body)
		if err != nil {
			return nil, err
		}
		reader = bytes.NewReader(buf)
	}

	req, err := http.NewRequestWithContext(ctx, method, u, reader)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json")
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if c.APIKey != "" {
		req.Header.Set("X-ApiKey", c.APIKey)
	}
	ua := c.UserAgent
	if ua == "" {
		ua = DefaultUserAgent
	}
	req.Header.Set("User-Agent", ua)

	httpClient := c.HTTP
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 60 * time.Second}
	}
	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	var env envelope
	jsonErr := json.Unmarshal(raw, &env)

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		apiErr := &APIError{HTTPStatus: resp.StatusCode, Body: string(raw)}
		if jsonErr == nil {
			apiErr.Messages = messageTexts(env.Messages)
		}
		return nil, apiErr
	}
	if jsonErr != nil {
		return nil, fmt.Errorf("unexpected response from %s %s: %w", method, path, jsonErr)
	}
	if !env.IsSuccess {
		return nil, &APIError{HTTPStatus: resp.StatusCode, Messages: messageTexts(env.Messages), Body: string(raw)}
	}
	return &env, nil
}

func messageTexts(msgs []Message) []string {
	out := make([]string, 0, len(msgs))
	for _, m := range msgs {
		if s := strings.TrimSpace(m.Message); s != "" {
			out = append(out, s)
		} else if m.Key != "" {
			out = append(out, m.Key)
		}
	}
	return out
}

func decodeData(env *envelope, out any) error {
	if out == nil || len(env.Data) == 0 || string(env.Data) == "null" {
		return nil
	}
	return json.Unmarshal(env.Data, out)
}

func isNull(raw json.RawMessage) bool {
	s := strings.TrimSpace(string(raw))
	return s == "" || s == "null"
}
