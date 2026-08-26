from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator


Platform = Literal["Telegram", "Instagram", "VK", "YouTube", "LinkedIn", "MAX"]
Gender = Literal["М", "Ж", "Неважно"]


class GenerationRequest(BaseModel):
    theme: str = Field(min_length=1, max_length=500)
    audience: str = Field(default="", max_length=500)
    gender: Gender = "Неважно"
    age_from: int | None = Field(default=None, ge=0, le=120)
    age_to: int | None = Field(default=None, ge=0, le=120)
    pain: str = Field(default="", max_length=1000)
    platform: Platform = "Instagram"
    content_format: str = Field(default="Авто", max_length=100)
    tone: str = Field(default="Авто", max_length=100)
    additional_wishes: str = Field(default="", max_length=1500)
    ai_prompt: str = Field(default="", max_length=3000)
    reference_analysis: str = Field(default="", max_length=12000)

    @field_validator("theme", "audience", "pain", "content_format", "tone", "additional_wishes", "ai_prompt", "reference_analysis")
    @classmethod
    def strip_text(cls, value: str) -> str:
        return value.strip()

    def model_post_init(self, __context: Any) -> None:
        if self.age_from is not None and self.age_to is not None and self.age_from > self.age_to:
            raise ValueError("Минимальный возраст не может быть больше максимального")


class BriefsPayload(BaseModel):
    subtleties: dict[str, Any] | None = None
    copywriter_brief: dict[str, Any]
    designer_brief: dict[str, Any]


class ReferenceRequest(BaseModel):
    url: str = Field(min_length=8, max_length=2000)


class ReferenceResponse(BaseModel):
    tone: str
    structure: str
    visual_style: str
    key_themes: list[str]
    emotional_impact: str


class FieldVisibilityState(BaseModel):
    enabled: bool = True


class PromptSettingsPayload(BaseModel):
    system_prompt: str = Field(min_length=1, max_length=12000)
    example_response: BriefsPayload
    analysis_prompt: str = Field(min_length=1, max_length=5000)
    analysis_usage: str = Field(min_length=1, max_length=5000)
    hide_subtleties: bool = False
    field_visibility: dict[str, FieldVisibilityState] = Field(default_factory=dict)


class VersionCreate(BaseModel):
    copywriter_brief: dict[str, Any]
    designer_brief: dict[str, Any]
    user_prompt: str = ""
    parent_version_id: int | None = None


class VersionResponse(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    generation_id: int
    version_number: int
    copywriter_brief: dict[str, Any]
    designer_brief: dict[str, Any]
    is_active: bool
    created_at: datetime
    user_prompt: str
    parent_version_id: int | None


class VersionList(BaseModel):
    versions: list[VersionResponse]


class RegenerationRequest(BaseModel):
    prompt: str = Field(min_length=1, max_length=3000)

    @field_validator("prompt")
    @classmethod
    def strip_prompt(cls, value: str) -> str:
        return value.strip()


class SubtletiesUpdate(BaseModel):
    deadline: str = Field(min_length=1, max_length=200)
    revisions: str = Field(min_length=1, max_length=200)
    delivery_format: str = Field(min_length=1, max_length=500)
    priority: str = Field(min_length=1, max_length=100)

    @field_validator("deadline", "revisions", "delivery_format", "priority")
    @classmethod
    def strip_text(cls, value: str) -> str:
        return value.strip()


class GenerationResponse(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    created_at: datetime
    input_data: GenerationRequest
    result: BriefsPayload
    current_version_id: int | None = None
    versions: list[VersionResponse] = []


class HistoryItem(BaseModel):
    id: int
    created_at: datetime
    theme: str
    platform: str


class SheetConnectRequest(BaseModel):
    sheet_url: str = Field(min_length=1, max_length=500)
    sheet_name: str = Field(default="Сентябрь", min_length=1, max_length=200)


class PostPlanBase(BaseModel):
    publish_date: datetime
    theme: str
    rubric: str = ""
    description: str = ""
    idea: str | None = None
    status: str = ""
    client_comment: str | None = None


class PostPlanCreate(PostPlanBase):
    sheet_url: str = Field(min_length=1, max_length=500)
    sheet_name: str = Field(default="Сентябрь", min_length=1, max_length=200)
    row_index: int = Field(ge=2)


class PostPlanResponse(PostPlanBase):
    model_config = ConfigDict(from_attributes=True)

    id: int
    sheet_url: str
    sheet_name: str
    row_index: int
    brief_id: int | None = None
    brief_status: Literal["not_created", "created", "error"] = "not_created"
    created_at: datetime
    updated_at: datetime
