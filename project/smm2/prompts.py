import json
from typing import Any

from schemas import GenerationRequest


DEFAULT_SYSTEM_PROMPT = """Ты — креативный директор SMM-агентства с 10-летним опытом.
Создавай конкретные, применимые ТЗ на русском языке. Используй яркие образы и интригу.
Не используй шаблонные слова «уникальный», «инновационный», «качественный»."""
DEFAULT_ANALYSIS_PROMPT = """Проанализируй пост и извлеки:
- Tone of voice (какой стиль общения)
- Структуру (как построен пост)
- Визуальный стиль (если есть описание)
- Ключевые темы и идеи
- Эмоциональное воздействие"""
DEFAULT_ANALYSIS_USAGE = """Используй эти элементы при создании ТЗ:
- Скопируй tone of voice
- Используй похожую структуру
- Добавь визуальные приёмы в раздел дизайнера
- Включи ключевые темы в основную часть"""

RESULT_SCHEMA = {
    "name": "smm_briefs",
    "schema": {
        "type": "object",
        "additionalProperties": False,
        # subtleties добавляется обычным режимом, но отключается настройкой hide_subtleties.
        "required": ["copywriter_brief", "designer_brief"],
        "properties": {
            "subtleties": {"type": "object", "additionalProperties": True},
            "copywriter_brief": {"type": "object", "additionalProperties": True},
            "designer_brief": {"type": "object", "additionalProperties": True},
        },
    },
}

EXAMPLE_RESPONSE: dict[str, Any] = {
    "subtleties": {"key_message": "Комфортная жизнь начинается с продуманного дома", "deadline": "24 часа", "revisions": "2 итерации", "delivery_format": "Копирайтер: .docx; Дизайнер: .fig", "priority": "Высокий"},
    "copywriter_brief": {
        "concept": "Показать, как пространство жилого комплекса помогает жить, работать и отдыхать каждый день.",
        "goal": "Вовлечь аудиторию и показать преимущества проекта.",
        "tone": "Атмосферный, уверенный, человечный.",
        "structure": {"headlines": ["Дом, в который хочется возвращаться", "Когда город рядом, а шум — нет"], "hooks": ["Представьте утро, в котором не нужно выбирать между городом и тишиной.", "Каждый день начинается там, где вам по-настоящему спокойно."], "main_points": ["Расскажите о территории и инфраструктуре.", "Добавьте конкретную пользу для жителей."], "conclusion": "Выберите пространство, в котором удобно быть собой.", "ctas": ["Узнайте подробности и запишитесь на просмотр.", "Оставьте заявку, чтобы получить консультацию."]},
        "volume": "800–1200 знаков с пробелами", "keywords": ["жилой комплекс", "комфорт", "инфраструктура"], "stop_words": ["уникальный", "инновационный"], "recommendations": "Использовать короткие абзацы, конкретные детали и понятный призыв.",
    },
    "designer_brief": {"visual_task": "Передать ощущение света, простора и спокойствия.", "format": "Карусель", "palettes": ["#2E7D32 — зелёный", "#FFFFFF — белый", "#FFB300 — акцентный жёлтый"], "required_elements": ["Логотип", "Кнопка призыва", "Карточки с фактами"], "composition": "Первый слайд — сильный заголовок и образ пространства; далее — преимущества; финал — призыв.", "style": "Фотографичный минимализм, натуральные цвета.", "references": ["Lifestyle-посты о жилых пространствах"], "technical_requirements": "1080×1080, PNG, размер до 2 МБ"},
}

FIELD_PATHS = (
    "copywriter_brief.concept", "copywriter_brief.goal", "copywriter_brief.tone",
    "copywriter_brief.structure.headlines", "copywriter_brief.structure.hooks",
    "copywriter_brief.structure.main_points", "copywriter_brief.structure.conclusion",
    "copywriter_brief.structure.ctas", "copywriter_brief.volume", "copywriter_brief.keywords",
    "copywriter_brief.stop_words", "copywriter_brief.recommendations", "designer_brief.visual_task",
    "designer_brief.format", "designer_brief.palettes", "designer_brief.required_elements",
    "designer_brief.composition", "designer_brief.style", "designer_brief.references",
    "designer_brief.technical_requirements",
)


def default_field_visibility() -> dict[str, dict[str, bool]]:
    """Стандартно все поля ТЗ участвуют в генерации и отображении."""
    return {path: {"enabled": True} for path in FIELD_PATHS}


def normalize_field_visibility(value: Any) -> dict[str, dict[str, bool]]:
    """Дополняет сохранённые настройки состояниями для появившихся полей."""
    visibility = default_field_visibility()
    if not isinstance(value, dict):
        return visibility
    for path in FIELD_PATHS:
        state = value.get(path)
        if isinstance(state, dict) and isinstance(state.get("enabled"), bool):
            visibility[path] = {"enabled": state["enabled"]}
    return visibility


def filter_enabled_fields(payload: dict[str, Any], field_visibility: dict[str, Any] | None = None) -> dict[str, Any]:
    """Возвращает копию ТЗ без полей, отключённых в настройках промпта."""
    filtered = json.loads(json.dumps(payload, ensure_ascii=False))
    visibility = normalize_field_visibility(field_visibility)
    for path, state in visibility.items():
        if state["enabled"]:
            continue
        keys = path.split(".")
        parent = filtered
        for key in keys[:-1]:
            parent = parent.get(key) if isinstance(parent, dict) else None
            if parent is None:
                break
        if isinstance(parent, dict):
            parent.pop(keys[-1], None)
    if not filtered.get("copywriter_brief", {}).get("structure"):
        filtered.get("copywriter_brief", {}).pop("structure", None)
    return filtered


def default_prompt_settings() -> dict[str, Any]:
    return {"system_prompt": DEFAULT_SYSTEM_PROMPT, "example_response": EXAMPLE_RESPONSE, "analysis_prompt": DEFAULT_ANALYSIS_PROMPT, "analysis_usage": DEFAULT_ANALYSIS_USAGE, "hide_subtleties": False, "field_visibility": default_field_visibility()}


def get_prompt_settings() -> dict[str, Any]:
    """Возвращает единый объект сохранённых настроек или безопасные стандарты."""
    defaults = default_prompt_settings()
    try:
        from main import SessionLocal
        from models import PromptSettings

        db = SessionLocal()
        try:
            item = db.query(PromptSettings).filter_by(name="prompt_settings").first()
            if not item or not isinstance(item.content, dict):
                return defaults
            settings = {**defaults, **item.content}
            required = {"copywriter_brief", "designer_brief"}
            if not required.issubset(settings["example_response"]):
                settings["example_response"] = EXAMPLE_RESPONSE
            settings["field_visibility"] = normalize_field_visibility(settings.get("field_visibility"))
            return settings
        finally:
            db.close()
    except Exception:
        return defaults


def build_prompt(data: GenerationRequest | dict) -> str:
    values = data if isinstance(data, dict) else data.model_dump()
    settings = get_prompt_settings()
    hide_subtleties = settings["hide_subtleties"]
    example = filter_enabled_fields(settings["example_response"], settings["field_visibility"])
    if hide_subtleties:
        example.pop("subtleties", None)
    required = '"copywriter_brief" и "designer_brief"' if hide_subtleties else '"subtleties", "copywriter_brief" и "designer_brief"'
    subtleties_instruction = "Не создавай ключ subtleties." if hide_subtleties else "Заполни блок subtleties: ключевой месседж, срок, правки, формат сдачи и приоритет."
    return f"""Верни только валидный JSON без markdown и пояснений.

{settings["system_prompt"]}

Создай конкретное SMM-ТЗ для темы пользователя. Не используй заполнители вроде «Заголовок 1».
Пример структуры и глубины ответа:
{json.dumps(example, ensure_ascii=False, indent=2)}

Данные пользователя:
{json.dumps(values, ensure_ascii=False, indent=2)}

Используй тему, аудиторию, платформу, формат, тон, боль и дополнительные пожелания. Учти анализ референса: {values.get("reference_analysis") or "не указан"}.
{subtleties_instruction}
Корневой JSON-объект обязан содержать ключи {required}. Не изменяй названия этих ключей.
Все значения пиши на русском языке, кроме HEX-кодов цветов."""


def build_correction_prompt(original_brief: dict, data: GenerationRequest | dict, ai_prompt: str) -> str:
    settings = get_prompt_settings()
    return f"""{settings["system_prompt"]}
Исправь исходное ТЗ по инструкции пользователя, сохранив его JSON-структуру и все обязательные ключи.
Исходное ТЗ: {json.dumps(filter_enabled_fields(original_brief, settings["field_visibility"]), ensure_ascii=False)}
Исходные данные: {json.dumps(data if isinstance(data, dict) else data.model_dump(), ensure_ascii=False)}
Инструкция: {ai_prompt}
Верни только валидный JSON."""


def build_copywriter_regeneration_prompt(copywriter_brief: dict, designer_brief: dict, user_prompt: str) -> str:
    settings = get_prompt_settings()
    briefs = filter_enabled_fields({"copywriter_brief": copywriter_brief, "designer_brief": designer_brief}, settings["field_visibility"])
    return f"""{settings["system_prompt"]}
Перепиши ТЗ копирайтера по правкам пользователя, сохранив совместимость с ТЗ дизайнера.
Копирайтер: {json.dumps(briefs["copywriter_brief"], ensure_ascii=False)}
Дизайнер: {json.dumps(briefs["designer_brief"], ensure_ascii=False)}
Правки: {user_prompt}
Верни только JSON вида {{"copywriter_brief": {{...}}}}."""


def build_designer_regeneration_prompt(copywriter_brief: dict, designer_brief: dict, user_prompt: str) -> str:
    settings = get_prompt_settings()
    briefs = filter_enabled_fields({"copywriter_brief": copywriter_brief, "designer_brief": designer_brief}, settings["field_visibility"])
    return f"""{settings["system_prompt"]}
Перепиши ТЗ дизайнера по правкам пользователя и согласуй его с ТЗ копирайтера.
Копирайтер: {json.dumps(briefs["copywriter_brief"], ensure_ascii=False)}
Дизайнер: {json.dumps(briefs["designer_brief"], ensure_ascii=False)}
Правки: {user_prompt}
Верни только JSON вида {{"designer_brief": {{...}}}}."""


def analyze_reference_prompt(content: str) -> str:
    settings = get_prompt_settings()
    return f"""{settings["system_prompt"]}
{settings["analysis_prompt"]}

Верни только JSON с ключами tone, structure, visual_style, key_themes (массив строк), emotional_impact.
Контент референса: {content[:12000]}

При создании будущего ТЗ будет применено: {settings["analysis_usage"]}"""
