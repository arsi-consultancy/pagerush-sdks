"""Official Python SDK for the PageRush API: Typst templates rendered to PDF.

Example::

    import pagerush

    client = pagerush.PageRush()  # reads PAGERUSH_API_KEY
    result = client.templates.render("tpl_123", data={"name": "Ada"})
    result.save("invoice.pdf")

See :class:`PageRush` for configuration and :mod:`pagerush.webhooks` for verifying deliveries.
"""

from . import webhooks
from ._client import DEFAULT_BASE_URL, PageRush
from ._errors import PageRushConnectionError, PageRushError, WebhookVerificationError
from ._files import TEXT_EXTENSIONS, file_from_path, template_file
from ._http import HTTPRequest, HTTPResponse, Transport, urllib_transport
from ._resources import Jobs, RenderResult, Templates
from ._types import (
    ErrorItem,
    Job,
    JobDelivery,
    JobError,
    JobStatus,
    Template,
    TemplateFile,
    TemplateList,
    TemplateVersion,
    TemplateVersionSummary,
    TemplateWithVersion,
)
from ._version import __version__
from .webhooks import WebhookEvent, Webhooks

__all__ = [
    "DEFAULT_BASE_URL",
    "TEXT_EXTENSIONS",
    "ErrorItem",
    "HTTPRequest",
    "HTTPResponse",
    "Job",
    "JobDelivery",
    "JobError",
    "JobStatus",
    "Jobs",
    "PageRush",
    "PageRushConnectionError",
    "PageRushError",
    "RenderResult",
    "Template",
    "TemplateFile",
    "TemplateList",
    "TemplateVersion",
    "TemplateVersionSummary",
    "TemplateWithVersion",
    "Templates",
    "Transport",
    "WebhookEvent",
    "WebhookVerificationError",
    "Webhooks",
    "__version__",
    "file_from_path",
    "template_file",
    "urllib_transport",
    "webhooks",
]
