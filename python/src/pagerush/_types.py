"""Typed shapes of API resources.

Resources are plain ``dict`` objects at runtime; these ``TypedDict`` classes only describe them for
type checkers and editors. Field names are the API's own snake_case names.
"""

from __future__ import annotations

from typing import Any, Dict, List, Literal, Optional, TypedDict

__all__ = [
    "ErrorItem",
    "Job",
    "JobDelivery",
    "JobError",
    "JobStatus",
    "Template",
    "TemplateFile",
    "TemplateList",
    "TemplateVersion",
    "TemplateVersionSummary",
    "TemplateWithVersion",
]

JobStatus = Literal["queued", "rendering", "succeeded", "failed"]
"""A job's status. ``succeeded`` and ``failed`` are final."""


class ErrorItem(TypedDict):
    """One entry of an error's ``errors`` list: a field path (or Typst location) and a message."""

    path: str
    message: str


class TemplateFile(TypedDict):
    """A file in a template version besides ``main.typ``.

    ``encoding`` is ``"utf8"`` for text files and ``"base64"`` for binary files (images, fonts).
    Build one with :func:`pagerush.template_file` or :func:`pagerush.file_from_path`.
    """

    path: str
    content: str
    encoding: Literal["utf8", "base64"]


class TemplateVersionSummary(TypedDict):
    """A version as listed in ``Template["versions"]``."""

    version: int
    live: bool
    bytes: int
    file_count: int
    has_sample_data: bool
    typst_version: str
    created_at: str


class _TemplateBase(TypedDict):
    id: str
    name: str
    live_version: Optional[int]
    latest_version: int
    created_at: str
    updated_at: str


class Template(_TemplateBase, total=False):
    """A template.

    ``live_version`` is the version renders use unless they ask for one (``None`` until a version is
    published). ``versions`` (newest first) is present only on :meth:`Templates.get`.
    """

    versions: List[TemplateVersionSummary]


class TemplateVersion(TypedDict):
    """One immutable version of a template: its ``main.typ`` source and other files."""

    version: int
    source: str
    document: Optional[Dict[str, Any]]
    files: List[TemplateFile]
    sample_data: Any
    typst_version: str
    created_at: str


class TemplateList(TypedDict):
    """One page of templates. Pass ``next_cursor`` as ``cursor`` for the next page; ``None`` on the
    last page."""

    items: List[Template]
    next_cursor: Optional[str]


class TemplateWithVersion(TypedDict):
    """Returned by :meth:`Templates.create` and :meth:`Templates.create_version`."""

    template: Template
    version: TemplateVersion


class JobError(TypedDict):
    """Why a job failed."""

    code: Optional[str]
    message: Optional[str]


class JobDelivery(TypedDict):
    """Callback delivery state for a job rendered with ``callback_url``."""

    status: Literal["pending", "delivered", "failed"]
    attempts: int
    error: Optional[str]


class Job(TypedDict):
    """A render job.

    ``download_url`` is a signed link to the PDF, valid for up to 1 hour. It is ``None`` until the
    job succeeds, and once the file has expired (24 hours) or was removed by
    ``delete_after_download``. Fetch the job again for a fresh link.
    """

    id: str
    status: JobStatus
    mode: Literal["sync", "async"]
    region: str
    template_id: Optional[str]
    template_version: Optional[int]
    engine: Optional[str]
    bytes: Optional[int]
    duration_ms: Optional[int]
    error: Optional[JobError]
    delivery: Optional[JobDelivery]
    metadata: Optional[Dict[str, str]]
    download_url: Optional[str]
    delete_after_download: bool
    created_at: str
    completed_at: Optional[str]
