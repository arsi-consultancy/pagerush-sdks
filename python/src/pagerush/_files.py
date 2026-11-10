"""Helpers that build template file entries."""

from __future__ import annotations

import base64
import os
from typing import Optional, Union

from ._types import TemplateFile

__all__ = ["TEXT_EXTENSIONS", "file_from_path", "template_file"]

TEXT_EXTENSIONS = frozenset({".typ", ".txt", ".csv", ".json", ".svg", ".xml", ".md", ".yml", ".yaml", ".bib"})
"""Extensions sent as ``utf8``. Every other file is sent as ``base64``."""


def _is_text(path: str) -> bool:
    return os.path.splitext(path)[1].lower() in TEXT_EXTENSIONS


def template_file(path: str, contents: Union[str, bytes]) -> TemplateFile:
    """Build a template file entry ``{"path", "content", "encoding"}``.

    Text files (``.typ .txt .csv .json .svg .xml .md .yml .yaml .bib``) are sent as ``utf8``;
    everything else (images, fonts), and text files whose bytes are not valid UTF-8, as ``base64``.

    Args:
        path: Path inside the template, relative to ``main.typ`` (e.g. ``"assets/logo.png"``).
        contents: The file's contents. ``str`` is encoded as UTF-8.

    Example::

        logo = pagerush.template_file("logo.png", open("logo.png", "rb").read())
    """
    raw = contents.encode("utf-8") if isinstance(contents, str) else contents
    if _is_text(path):
        try:
            return {"path": path, "content": raw.decode("utf-8"), "encoding": "utf8"}
        except UnicodeDecodeError:
            pass
    return {"path": path, "content": base64.b64encode(raw).decode("ascii"), "encoding": "base64"}


def file_from_path(
    local_path: Union[str, os.PathLike[str]], target_path: Optional[str] = None
) -> TemplateFile:
    """Read a local file into a template file entry.

    Args:
        local_path: The file on disk.
        target_path: Path inside the template. Defaults to the file's base name.
    """
    with open(local_path, "rb") as f:
        data = f.read()
    return template_file(target_path or os.path.basename(os.fspath(local_path)), data)
