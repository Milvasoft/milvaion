// Package config stores the CLI's connection settings.
package config

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

// Environment variables that override the config file.
const (
	EnvURL    = "MILVAION_URL"
	EnvAPIKey = "MILVAION_API_KEY"
	EnvConfig = "MILVAION_CONFIG"
)

// Config is what `milvaion login` saves.
type Config struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"apiKey"`
}

// Path returns the config file location: $MILVAION_CONFIG, or milvaion/config.yaml
// under the user's config directory (~/.config on Linux, %AppData% on Windows,
// ~/Library/Application Support on macOS).
func Path() (string, error) {
	if p := os.Getenv(EnvConfig); p != "" {
		return p, nil
	}
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", fmt.Errorf("cannot find the user config directory, set %s: %w", EnvConfig, err)
	}
	return filepath.Join(dir, "milvaion", "config.yaml"), nil
}

// Load reads the config file. A missing file yields an empty config.
func Load() (Config, error) {
	var c Config
	p, err := Path()
	if err != nil {
		return c, err
	}
	b, err := os.ReadFile(p)
	if errors.Is(err, os.ErrNotExist) {
		return c, nil
	}
	if err != nil {
		return c, err
	}
	if err := yaml.Unmarshal(b, &c); err != nil {
		return c, fmt.Errorf("%s: %w", p, err)
	}
	return c, nil
}

// Save writes the config file, readable only by the current user.
func Save(c Config) (string, error) {
	p, err := Path()
	if err != nil {
		return "", err
	}
	if err := os.MkdirAll(filepath.Dir(p), 0o700); err != nil {
		return "", err
	}
	b, err := yaml.Marshal(c)
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(p, b, 0o600); err != nil {
		return "", err
	}
	return p, nil
}

// Resolve applies, in increasing priority, the config file, the environment and
// explicit flag values.
func Resolve(flagURL, flagKey string) (Config, error) {
	c, err := Load()
	if err != nil {
		return c, err
	}
	if v := os.Getenv(EnvURL); v != "" {
		c.URL = v
	}
	if v := os.Getenv(EnvAPIKey); v != "" {
		c.APIKey = v
	}
	if flagURL != "" {
		c.URL = flagURL
	}
	if flagKey != "" {
		c.APIKey = flagKey
	}
	return c, nil
}
