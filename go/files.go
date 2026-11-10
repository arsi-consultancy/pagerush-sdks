package pagerush

import (
	"encoding/base64"
	"os"
	"path/filepath"
	"strings"
)

// File encodings for TemplateFile.Encoding.
const (
	EncodingUTF8   = "utf8"
	EncodingBase64 = "base64"
)

// TemplateFile is a file a template imports or reads, besides main.typ: more
// .typ modules, images, fonts or data. Path is relative to main.typ and uses
// forward slashes on every platform, such as "parts/header.typ" or
// "logo.png".
type TemplateFile struct {
	Path    string `json:"path"`
	Content string `json:"content"`
	// Encoding is EncodingUTF8 for text or EncodingBase64 for binary content.
	Encoding string `json:"encoding"`
}

var textExtensions = map[string]bool{
	".typ": true, ".txt": true, ".csv": true, ".json": true, ".svg": true,
	".xml": true, ".md": true, ".yml": true, ".yaml": true, ".bib": true,
}

// NewTemplateFile builds a TemplateFile for path from raw contents. path is
// used as given, so it must use forward slashes. Files
// ending in .typ, .txt, .csv, .json, .svg, .xml, .md, .yml, .yaml or .bib are
// sent as utf8 text; everything else (images, fonts) as base64.
func NewTemplateFile(path string, contents []byte) TemplateFile {
	if textExtensions[strings.ToLower(filepath.Ext(path))] {
		return TemplateFile{Path: path, Content: string(contents), Encoding: EncodingUTF8}
	}
	return TemplateFile{Path: path, Content: base64.StdEncoding.EncodeToString(contents), Encoding: EncodingBase64}
}

// TemplateFileFromPath reads localPath and builds a TemplateFile for it, as
// NewTemplateFile does. targetPath is the file's path inside the template; if
// empty, the local file's base name is used. targetPath is converted to
// forward slashes with filepath.ToSlash, so OS-specific separators work.
func TemplateFileFromPath(localPath, targetPath string) (TemplateFile, error) {
	contents, err := os.ReadFile(localPath)
	if err != nil {
		return TemplateFile{}, err
	}
	if targetPath == "" {
		targetPath = filepath.Base(localPath)
	}
	return NewTemplateFile(filepath.ToSlash(targetPath), contents), nil
}
