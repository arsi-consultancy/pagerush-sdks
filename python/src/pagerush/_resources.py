"""API resources: templates and jobs."""

from __future__ import annotations

import os
import re
import urllib.parse
import uuid
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Dict, Iterator, Mapping, Optional, Sequence, Union, cast

from ._errors import PageRushError, validation_error
from ._types import Job, Template, TemplateFile, TemplateList, TemplateVersion, TemplateWithVersion

if TYPE_CHECKING:
    from ._client import PageRush

__all__ = ["Jobs", "RenderResult", "Templates"]

_FINAL_STATUSES = ("succeeded", "failed")
_FILENAME = re.compile(r'filename="([^"]*)"')
_IDEMPOTENCY_KEY = re.compile(r"[A-Za-z0-9_\-:.]{1,255}")


def _seg(value: Union[str, int]) -> str:
    return urllib.parse.quote(str(value), safe="")


def _int_header(value: Optional[str]) -> Optional[int]:
    if value is None:
        return None
    try:
        return int(value.strip())
    except ValueError:
        return None


@dataclass
class RenderResult:
    """The outcome of :meth:`Templates.render`.

    When the PDF came back directly (HTTP 200), ``queued`` is ``False`` and ``pdf`` holds the bytes.
    When the render was queued (HTTP 202: ``callback_url`` was set, or ``prefer_async=True`` and the
    renderer was busy), ``queued`` is ``True``, ``pdf`` is ``None`` and ``job`` holds the queued
    job; use :meth:`Jobs.wait` and :meth:`Jobs.download`, or wait for the webhook.

    Attributes:
        queued: Whether the render was queued instead of returned.
        pdf: The PDF bytes, or ``None`` when queued.
        job_id: The job id (``X-Job-Id``, or the queued job's id).
        template_version: The template version rendered (``X-Template-Version``, or the job's).
        engine: The compiler that produced the PDF (``X-Render-Engine``), or ``None``.
        page_count: Pages in the PDF (``X-Page-Count``), or ``None``.
        warnings: Number of Typst warnings (``X-Template-Warnings``), or ``None``.
        request_id: ``X-Request-Id``, or ``None``.
        filename: The file name from ``Content-Disposition``, or ``None``.
        job: The queued job (HTTP 202 only), else ``None``.
    """

    queued: bool
    pdf: Optional[bytes] = field(repr=False)
    job_id: Optional[str]
    template_version: Optional[int]
    engine: Optional[str] = None
    page_count: Optional[int] = None
    warnings: Optional[int] = None
    request_id: Optional[str] = None
    filename: Optional[str] = None
    job: Optional[Job] = None

    def save(self, path: Union[str, os.PathLike[str]]) -> None:
        """Write the PDF to ``path``.

        Raises:
            ValueError: The render was queued, so there is no PDF yet.
        """
        if self.pdf is None:
            raise ValueError("This render was queued and has no PDF yet; use jobs.wait() and jobs.download()")
        with open(path, "wb") as f:
            f.write(self.pdf)


def _content(
    *,
    source: Optional[str],
    document: Optional[Mapping[str, Any]],
    files: Optional[Sequence[TemplateFile]],
    sample_data: Any,
    typst_version: Optional[str],
) -> Dict[str, Any]:
    body: Dict[str, Any] = {}
    if source is not None:
        body["source"] = source
    if document is not None:
        body["document"] = dict(document)
    if files is not None:
        body["files"] = [dict(f) for f in files]
    if sample_data is not None:
        body["sample_data"] = sample_data
    if typst_version is not None:
        body["typst_version"] = typst_version
    return body


class Templates:
    """Template endpoints (``/v1/templates``). Use it as ``client.templates``."""

    def __init__(self, client: PageRush) -> None:
        self._client = client

    def list(self, *, limit: Optional[int] = None, cursor: Optional[str] = None) -> TemplateList:
        """List one page of templates.

        Args:
            limit: Page size, 1-100 (server default 50).
            cursor: ``next_cursor`` from the previous page.

        Returns:
            ``{"items": [...], "next_cursor": str | None}``.
        """
        response = self._client._request("GET", "/v1/templates", query={"limit": limit, "cursor": cursor})
        return cast(TemplateList, self._client._json_object(response, ("items", "next_cursor")))

    def list_all(self, *, limit: Optional[int] = None) -> Iterator[Template]:
        """Iterate over every template, fetching pages lazily until ``next_cursor`` is ``None``.

        Args:
            limit: Page size for each request.
        """
        cursor: Optional[str] = None
        while True:
            page = self.list(limit=limit, cursor=cursor)
            yield from page["items"]
            cursor = page["next_cursor"]
            if not cursor:
                return

    def create(
        self,
        *,
        name: str,
        source: Optional[str] = None,
        document: Optional[Mapping[str, Any]] = None,
        files: Optional[Sequence[TemplateFile]] = None,
        sample_data: Any = None,
        typst_version: Optional[str] = None,
    ) -> TemplateWithVersion:
        """Create a template with its first version, published.

        Send exactly one of ``source`` (the ``main.typ`` Typst source) or ``document`` (a visual
        editor document). The template reads render data with ``#let data = json("data.json")``.

        Args:
            name: Template name (1-120 characters).
            source: Typst source for ``main.typ``.
            document: A visual-editor document, instead of ``source``.
            files: Other files the template reads (see :func:`pagerush.template_file`).
            sample_data: Example data for dashboard test renders.
            typst_version: Typst release to render with (server default if omitted).

        Returns:
            ``{"template": Template, "version": TemplateVersion}``.
        """
        body = {"name": name}
        body.update(
            _content(
                source=source,
                document=document,
                files=files,
                sample_data=sample_data,
                typst_version=typst_version,
            )
        )
        response = self._client._request(
            "POST", "/v1/templates", json_body=body, has_body=True, retry="unsafe"
        )
        return cast(TemplateWithVersion, self._client._json_object(response, ("template", "version")))

    def get(self, template_id: str) -> Template:
        """Get a template, including its ``versions`` list (newest first)."""
        response = self._client._request("GET", f"/v1/templates/{_seg(template_id)}")
        return cast(Template, self._client._unwrap(response, "template"))

    def rename(self, template_id: str, name: str) -> Template:
        """Rename a template and return it."""
        response = self._client._request(
            "PATCH",
            f"/v1/templates/{_seg(template_id)}",
            json_body={"name": name},
            has_body=True,
            retry="unsafe",
        )
        return cast(Template, self._client._unwrap(response, "template"))

    def delete(self, template_id: str) -> None:
        """Delete a template and all its versions."""
        self._client._request("DELETE", f"/v1/templates/{_seg(template_id)}")

    def create_version(
        self,
        template_id: str,
        *,
        source: Optional[str] = None,
        document: Optional[Mapping[str, Any]] = None,
        files: Optional[Sequence[TemplateFile]] = None,
        sample_data: Any = None,
        typst_version: Optional[str] = None,
        publish: Optional[bool] = None,
    ) -> TemplateWithVersion:
        """Add a new version to a template.

        Args:
            template_id: The template.
            source: Typst source for ``main.typ`` (or send ``document``).
            document: A visual-editor document, instead of ``source``.
            files: Other files the template reads.
            sample_data: Example data for dashboard test renders.
            typst_version: Typst release; defaults to the previous version's.
            publish: Make it the live version (server default ``True``). Pass ``False`` to add a
                draft you can test with ``render(..., version=n)``.

        Returns:
            ``{"template": Template, "version": TemplateVersion}``.
        """
        body = _content(
            source=source,
            document=document,
            files=files,
            sample_data=sample_data,
            typst_version=typst_version,
        )
        if publish is not None:
            body["publish"] = publish
        response = self._client._request(
            "POST",
            f"/v1/templates/{_seg(template_id)}/versions",
            json_body=body,
            has_body=True,
            retry="unsafe",
        )
        return cast(TemplateWithVersion, self._client._json_object(response, ("template", "version")))

    def get_version(self, template_id: str, version: int) -> TemplateVersion:
        """Get one version's source, files and sample data."""
        response = self._client._request("GET", f"/v1/templates/{_seg(template_id)}/versions/{_seg(version)}")
        return cast(TemplateVersion, self._client._unwrap(response, "version"))

    def publish(self, template_id: str, version: int) -> Template:
        """Make ``version`` the live version. Publishing an older version is how you roll back."""
        response = self._client._request(
            "POST",
            f"/v1/templates/{_seg(template_id)}/publish",
            json_body={"version": version},
            has_body=True,
            retry="unsafe",
        )
        return cast(Template, self._client._unwrap(response, "template"))

    def render(
        self,
        template_id: str,
        *,
        data: Any,
        version: Optional[int] = None,
        filename: Optional[str] = None,
        metadata: Optional[Mapping[str, str]] = None,
        delete_after_download: Optional[bool] = None,
        callback_url: Optional[str] = None,
        idempotency_key: Optional[str] = None,
        prefer_async: bool = False,
    ) -> RenderResult:
        """Render a template with ``data`` to PDF.

        Every call sends an ``Idempotency-Key`` (a random UUID unless you pass one) and reuses it
        on retries, so a retried request never renders or charges twice. A retry that arrives while
        the first attempt is still rendering gets 409 ``CONFLICT`` and is retried after
        ``Retry-After``. With ``delete_after_download=True`` the PDF is not stored, so a retry after
        a lost response gets 410 ``FILE_EXPIRED``.

        Args:
            template_id: The template.
            data: Any JSON value; the template reads it with ``json("data.json")``.
            version: Render this version instead of the live one.
            filename: File name for ``Content-Disposition`` (defaults to the template name).
            metadata: Up to 20 string pairs, echoed on the job and in the webhook.
            delete_after_download: Don't keep the PDF for 24 hours after you have it.
            callback_url: Queue the render and POST the signed PDF to this https URL.
            idempotency_key: Your own key: 1-255 characters from ``A-Z a-z 0-9 _ - : .``.
            prefer_async: Send ``Prefer: respond-async``. The API still renders straight away; only
                if that attempt hits a temporary capacity problem does it queue the job (HTTP 202)
                instead of failing with 503.

        Returns:
            A :class:`RenderResult`. Check ``result.queued``.

        Raises:
            PageRushError: On an API error, or with code ``VALIDATION_FAILED`` (status 0, nothing
                sent) if ``idempotency_key`` is invalid or the body cannot be encoded as JSON.
        """
        body: Dict[str, Any] = {"data": data}
        if version is not None:
            body["version"] = version
        if filename is not None:
            body["filename"] = filename
        if metadata is not None:
            body["metadata"] = dict(metadata)
        if delete_after_download is not None:
            body["delete_after_download"] = delete_after_download
        if callback_url is not None:
            body["callback_url"] = callback_url
        key = str(uuid.uuid4()) if idempotency_key is None else idempotency_key
        if not _IDEMPOTENCY_KEY.fullmatch(key):
            raise validation_error("idempotency_key must be 1-255 characters from A-Z a-z 0-9 _ - : .")
        headers = {"Idempotency-Key": key}
        if prefer_async:
            headers["Prefer"] = "respond-async"
        response = self._client._request(
            "POST",
            f"/v1/templates/{_seg(template_id)}/render",
            json_body=body,
            has_body=True,
            headers=headers,
            accept="application/pdf, application/json",
            retry="render",
        )
        if response.status == 202:
            job = cast(Job, self._client._json_object(response, ("id",)))
            return RenderResult(
                queued=True,
                pdf=None,
                job=job,
                job_id=job["id"],
                template_version=job.get("template_version"),
            )
        content_type = (response.header("Content-Type") or "").split(";")[0].strip().lower()
        if content_type != "application/pdf":
            raise self._client._invalid_response(
                response, f"Expected an application/pdf body, got {content_type or 'no Content-Type'}"
            )
        disposition = response.header("Content-Disposition") or ""
        match = _FILENAME.search(disposition)
        return RenderResult(
            queued=False,
            pdf=response.body,
            job_id=response.header("X-Job-Id"),
            template_version=_int_header(response.header("X-Template-Version")),
            engine=response.header("X-Render-Engine"),
            page_count=_int_header(response.header("X-Page-Count")),
            warnings=_int_header(response.header("X-Template-Warnings")),
            request_id=response.header("X-Request-Id"),
            filename=match.group(1) if match else None,
        )


class Jobs:
    """Job endpoints (``/v1/jobs``). Use it as ``client.jobs``."""

    def __init__(self, client: PageRush) -> None:
        self._client = client

    def get(self, job_id: str) -> Job:
        """Get a job's status and, once it succeeded, a fresh ``download_url``."""
        response = self._client._request("GET", f"/v1/jobs/{_seg(job_id)}")
        return cast(Job, self._client._json_object(response, ("id", "status")))

    def wait(self, job_id: str, *, timeout: float = 120.0, interval: float = 1.0) -> Job:
        """Poll a job until its status is ``succeeded`` or ``failed`` and return it.

        A failed job is returned, not raised; check ``job["status"]`` and ``job["error"]``.

        Args:
            job_id: The job.
            timeout: Give up after this many seconds.
            interval: Seconds between polls.

        Raises:
            PageRushError: Code ``WAIT_TIMEOUT`` (status 0) if the job is not finished in time.
        """
        clock = self._client._clock
        deadline = clock() + timeout
        while True:
            job = self.get(job_id)
            if job["status"] in _FINAL_STATUSES:
                return job
            remaining = deadline - clock()
            if remaining <= 0:
                raise PageRushError(
                    status=0,
                    code="WAIT_TIMEOUT",
                    message=f"WAIT_TIMEOUT: job {job_id} was still {job['status']} after {timeout}s",
                )
            self._client._sleep(min(interval, remaining))

    def download(self, job_id: str) -> bytes:
        """Download a finished job's PDF.

        Fetches the job, then its signed ``download_url`` (without your API key).

        Raises:
            PageRushError: Code ``FILE_EXPIRED`` if the job has no ``download_url`` (not finished,
                failed, expired after 24 hours, or deleted by ``delete_after_download``).
        """
        job = self.get(job_id)
        url = job.get("download_url")
        if not url:
            raise PageRushError(
                status=0,
                code="FILE_EXPIRED",
                detail=f"Job {job_id} has no download_url (status {job.get('status')}); "
                "the PDF is not ready, has expired, or was already downloaded",
            )
        if url.startswith("/"):
            url = self._client.base_url + url
        response = self._client._request("GET", url, absolute=True, auth=False, accept="application/pdf")
        return response.body
