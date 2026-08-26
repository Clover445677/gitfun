import json
import os

os.environ["DATABASE_URL"] = "sqlite:///./test_smm_briefs.db"

from fastapi.testclient import TestClient
import main
from main import SessionLocal, app
from datetime import datetime, timedelta

from models import Generation, PostPlan
from schemas import GenerationRequest


client = TestClient(app)


def test_clean_json_response_removes_wrapping_text_and_trailing_commas():
    raw = 'Пояснение\n{"subtleties": {}, "copywriter_brief": {}, "designer_brief": {},}\nГотово'
    cleaned = main.clean_json_response(raw)
    assert cleaned is not None
    assert cleaned == '{"subtleties": {}, "copywriter_brief": {}, "designer_brief": {}}'


def test_clean_json_response_returns_none_for_invalid_json():
    assert main.clean_json_response('{"subtleties": broken}') is None


def test_fix_keys_normalizes_short_model_keys():
    payload = {"subtleties": {}, "copywriter": {"concept": "Текст"}, "designer": {"style": "Минимализм"}}

    assert main.fix_keys(payload) == {
        "subtleties": {},
        "copywriter_brief": {"concept": "Текст"},
        "designer_brief": {"style": "Минимализм"},
    }


def test_fix_keys_keeps_existing_canonical_keys():
    payload = {"copywriter": {"concept": "Старый"}, "copywriter_brief": {"concept": "Новый"}}

    assert main.fix_keys(payload) == {"copywriter_brief": {"concept": "Новый"}}


def test_extract_regenerated_brief_accepts_wrapped_and_direct_response():
    assert main.extract_regenerated_brief({"designer_brief": {"style": "Минимализм"}}, "designer") == {"style": "Минимализм"}
    assert main.extract_regenerated_brief(
        {"visual_task": "Показать продукт", "format": "Карусель", "style": "Минимализм"},
        "designer",
    ) == {"visual_task": "Показать продукт", "format": "Карусель", "style": "Минимализм"}


def test_index_is_served():
    response = client.get("/")
    assert response.status_code == 200
    assert "SMM Briefs" in response.text


def test_history_is_valid_empty_list():
    response = client.get("/api/generations")
    assert response.status_code == 200
    assert isinstance(response.json(), list)


def test_invalid_generation_is_rejected():
    response = client.post("/api/generations", json={"theme": ""})
    assert response.status_code == 422


def test_max_platform_is_accepted():
    request = GenerationRequest(theme="Тест", platform="MAX")
    assert request.platform == "MAX"


def test_prompt_example_endpoints_require_all_brief_blocks():
    example = client.get("/api/prompts/example")
    assert example.status_code == 200
    assert set(example.json()) == {"subtleties", "copywriter_brief", "designer_brief"}

    invalid = client.post("/api/prompts/example", json={"subtleties": {}})
    assert invalid.status_code == 422


def test_prompt_settings_can_hide_subtleties():
    settings = client.get("/api/prompts").json()
    settings["hide_subtleties"] = True
    settings["example_response"].pop("subtleties", None)
    saved = client.post("/api/prompts", json=settings)
    assert saved.status_code == 200
    assert saved.json()["hide_subtleties"] is True
    assert "subtleties" not in saved.json()["example_response"]
    assert client.get("/api/prompts/default").json()["hide_subtleties"] is False


def test_prompt_settings_hide_individual_fields_in_result_and_export():
    settings = client.get("/api/prompts/default").json()
    settings["field_visibility"]["copywriter_brief.concept"] = {"enabled": False}
    settings["field_visibility"]["designer_brief.style"] = {"enabled": False}
    saved = client.post("/api/prompts", json=settings)
    assert saved.status_code == 200
    assert saved.json()["field_visibility"]["copywriter_brief.concept"] == {"enabled": False}

    payload = {
        "copywriter_brief": {"concept": "Не показывать", "goal": "Цель"},
        "designer_brief": {"style": "Не показывать", "format": "Карусель"},
    }
    filtered = main.filter_enabled_fields(payload, saved.json()["field_visibility"])
    assert "concept" not in filtered["copywriter_brief"]
    assert "style" not in filtered["designer_brief"]
    assert "Концепция" not in main.plain_text(filtered["copywriter_brief"])
    assert client.get("/api/prompts/default").status_code == 200


def test_fetch_reference_content_uses_system_proxies_and_telegram_domain(monkeypatch):
    class FakeResponse:
        text = "<html><body>Достаточно длинный публичный текст референса для проверки.</body></html>"

        def raise_for_status(self):
            pass

    captured = {}

    def fake_get(url, **kwargs):
        captured["url"] = url
        captured.update(kwargs)
        return FakeResponse()

    import requests
    import urllib.request

    monkeypatch.setattr(requests, "get", fake_get)
    monkeypatch.setattr(urllib.request, "getproxies", lambda: {"https": "http://127.0.0.1:8080"})
    monkeypatch.setattr(main.socket, "getaddrinfo", lambda *_: [(None, None, None, None, ("149.154.167.99", 0))])
    assert "референса" in main.fetch_reference_content("https://t.me/example/1")
    assert captured["url"] == "https://telegram.me/example/1"
    assert captured["timeout"] == 60
    assert captured["proxies"] == {"https": "http://127.0.0.1:8080"}


def test_generation_is_saved(monkeypatch):
    async def fake_generation(_):
        return {
            "subtleties": {"key_message": "Проверка", "deadline": "24 часа", "revisions": "2 итерации", "delivery_format": "DOCX", "priority": "Параллельно"},
            "copywriter_brief": {"concept": "Концепция", "goal": "Цель", "tone": "Экспертный", "structure": {"headlines": ["Заголовок"], "hooks": ["Крючок"], "main_points": ["Мысль"], "conclusion": "Вывод", "ctas": ["CTA"]}, "volume": "500 слов", "keywords": ["слово"], "stop_words": ["клише"], "recommendations": "Рекомендация"},
            "designer_brief": {"visual_task": "Задача", "format": "Карусель", "palettes": ["#FFFFFF"], "required_elements": ["Логотип"], "composition": "Центр", "style": "Минимализм", "references": ["Референс"], "technical_requirements": "1080x1080 PNG"},
        }

    monkeypatch.setattr(main, "generate_briefs", fake_generation)
    response = client.post("/api/generations", json={"theme": "Тестовая тема", "ai_prompt": "Используй дружелюбный стиль"})
    assert response.status_code == 201
    assert response.headers["content-type"].startswith("text/event-stream")
    events = response.text.split("\n\n")
    result_event = next(event for event in events if event.startswith("event: result"))
    result = json.loads(result_event.removeprefix("event: result\ndata: "))
    assert result["input_data"]["theme"] == "Тестовая тема"
    assert result["input_data"]["ai_prompt"] == "Используй дружелюбный стиль"
    assert len(result["versions"]) == 1


def test_regeneration_creates_versions(monkeypatch):
    result = {
        "subtleties": {"key_message": "Проверка", "deadline": "24 часа", "revisions": "2 итерации", "delivery_format": "DOCX", "priority": "Высокий"},
        "copywriter_brief": {"concept": "Концепция", "goal": "Цель", "tone": "Экспертный", "structure": {"headlines": ["Заголовок"], "hooks": ["Крючок"], "main_points": ["Мысль"], "conclusion": "Вывод", "ctas": ["CTA"]}, "volume": "500 слов", "keywords": ["слово"], "stop_words": ["клише"], "recommendations": "Рекомендация"},
        "designer_brief": {"visual_task": "Задача", "format": "Карусель", "palettes": ["#FFFFFF"], "required_elements": ["Логотип"], "composition": "Центр", "style": "Минимализм", "references": ["Референс"], "technical_requirements": "1080x1080 PNG"},
    }

    async def fake_generation(_):
        return result

    async def fake_regeneration(copywriter_brief, designer_brief, user_prompt, section):
        brief = dict(copywriter_brief if section == "copywriter" else designer_brief)
        brief["concept" if section == "copywriter" else "style"] = user_prompt
        return brief

    monkeypatch.setattr(main, "generate_briefs", fake_generation)
    monkeypatch.setattr(main, "regenerate_brief", fake_regeneration)
    response = client.post("/api/generations", json={"theme": "Версии"})
    result_event = next(event for event in response.text.split("\n\n") if event.startswith("event: result"))
    created = json.loads(result_event.removeprefix("event: result\ndata: "))
    generation_id = created["id"]
    copywriter = client.post(f"/api/generations/{generation_id}/regenerate/copywriter", json={"prompt": "Новый текст"})
    assert copywriter.status_code == 200
    designer = client.post(f"/api/generations/{generation_id}/regenerate/designer", json={"prompt": "Новый дизайн"})
    assert designer.status_code == 200
    versions = client.get(f"/api/generations/{generation_id}/versions").json()["versions"]
    assert len(versions) == 3
    assert client.get(f"/api/generations/{generation_id}/versions/{designer.json()['id']}").status_code == 200


def test_plan_lists_filters_and_generates_brief(monkeypatch):
    result = {
        "subtleties": {"key_message": "Проверка", "deadline": "24 часа", "revisions": "2 итерации", "delivery_format": "DOCX", "priority": "Высокий"},
        "copywriter_brief": {"concept": "Концепция", "goal": "Цель", "tone": "Экспертный", "structure": {"headlines": ["Заголовок"], "hooks": ["Крючок"], "main_points": ["Мысль"], "conclusion": "Вывод", "ctas": ["CTA"]}, "volume": "500 слов", "keywords": ["слово"], "stop_words": ["клише"], "recommendations": "Рекомендация"},
        "designer_brief": {"visual_task": "Задача", "format": "Карусель", "palettes": ["#FFFFFF"], "required_elements": ["Логотип"], "composition": "Центр", "style": "Минимализм", "references": ["Референс"], "technical_requirements": "1080x1080 PNG"},
    }

    async def fake_generation(_):
        return result

    monkeypatch.setattr(main, "generate_briefs", fake_generation)
    db = SessionLocal()
    marker = datetime.now().strftime("%Y%m%d%H%M%S%f")
    post = PostPlan(sheet_url=f"https://example.com/sheet/{marker}", row_index=2, publish_date=datetime.now() + timedelta(days=1), theme="Пост из плана", rubric="Новость", description="Описание", status="согласовано")
    db.add(post)
    db.commit()
    db.refresh(post)
    post_id = post.id
    db.close()

    pending = client.get("/api/plan/posts?filter=pending")
    assert pending.status_code == 200
    assert any(item["id"] == post_id and item["brief_status"] == "not_created" for item in pending.json())
    generated = client.post(f"/api/plan/generate/{post_id}")
    assert generated.status_code == 200
    ready = client.get("/api/plan/posts?filter=ready")
    assert any(item["id"] == post_id and item["brief_status"] == "created" for item in ready.json())


def test_import_plan_rows_maps_actual_sheet_headers():
    marker = datetime.now().strftime("%Y%m%d%H%M%S%f")
    sheet_url = f"https://example.com/sheet/{marker}"
    rows = [
        ["Контент-план на сентябрь"],
        [],
        ["Дата", "День", "Тема поста", "Рубрика", "Описание поста", "Сторис", "Статус", "Комментарий клиента"],
        ["01.09.2026", "вт", "С Днём знаний!", "Новость", "Поздравим с Днём знаний", "Опрос для аудитории", "фин согл", "Добавить фирменный цвет"],
    ]
    db = SessionLocal()
    assert main.import_plan_rows(sheet_url, rows, db) == 1
    post = db.query(PostPlan).filter_by(sheet_url=sheet_url, row_index=4).one()
    assert post.theme == "С Днём знаний!"
    assert post.description == "Поздравим с Днём знаний"
    assert post.idea == "Опрос для аудитории"
    assert post.client_comment == "Добавить фирменный цвет"
    db.close()


def test_import_plan_rows_maps_two_row_sheet_headers():
    marker = datetime.now().strftime("%Y%m%d%H%M%S%f")
    sheet_url = f"https://example.com/sheet/{marker}"
    rows = [
        ["", "Дата", "", "ВК и ТГ-канал", "", "", "", "", "Комментарий клиента"],
        ["", "", "", "Тема поста", "Рубрика", "Описание поста", "Сторис", "Статус", ""],
        ["1", "01.09", "вт", "С Днем знаний!", "Новость", "Поздравим с Днём знаний", "", "фин согл", ""],
    ]
    db = SessionLocal()
    assert main.import_plan_rows(sheet_url, rows, db, "Сентябрь") == 1
    post = db.query(PostPlan).filter_by(sheet_url=sheet_url, sheet_name="Сентябрь", row_index=3).one()
    assert post.theme == "С Днем знаний!"
    assert post.rubric == "Новость"
    assert post.status == "фин согл"
    db.close()


def test_saved_generation_read_delete_and_exports():
    result = {
        "subtleties": {"key_message": "Проверка", "deadline": "24 часа", "revisions": "2 итерации", "delivery_format": "DOCX", "priority": "Параллельно"},
        "copywriter_brief": {"concept": "Концепция", "goal": "Цель", "tone": "Экспертный", "structure": {"headlines": ["Заголовок 1"], "hooks": ["Крючок"], "main_points": ["Мысль"], "conclusion": "Вывод", "ctas": ["CTA"]}, "volume": "500 слов", "keywords": ["слово"], "stop_words": ["клише"], "recommendations": "Рекомендация"},
        "designer_brief": {"visual_task": "Задача", "format": "Карусель", "palettes": ["#FFFFFF"], "required_elements": ["Логотип"], "composition": "Центр", "style": "Минимализм", "references": ["Референс"], "technical_requirements": "1080x1080 PNG"},
    }
    db = SessionLocal()
    item = Generation(theme="Тест", platform="Telegram", input_data={"theme": "Тест"}, result=result)
    db.add(item)
    db.commit()
    db.refresh(item)
    generation_id = item.id
    db.close()

    assert client.get(f"/api/generations/{generation_id}").status_code == 200
    update = client.put(f"/api/generations/{generation_id}/subtleties", json={"deadline": "48 часов", "revisions": "3 итерации", "delivery_format": "DOCX", "priority": "Высокий"})
    assert update.status_code == 200
    assert update.json()["result"]["subtleties"]["deadline"] == "48 часов"
    docx = client.get(f"/api/generations/{generation_id}/export/docx")
    assert docx.status_code == 200
    assert docx.headers["content-type"].startswith("application/vnd.openxmlformats-officedocument")
    pdf = client.get(f"/api/generations/{generation_id}/export/pdf")
    assert pdf.status_code == 200
    assert pdf.headers["content-type"].startswith("application/pdf")
    copywriter_docx = client.get(f"/api/export/copywriter/docx/{generation_id}")
    assert copywriter_docx.status_code == 200
    assert copywriter_docx.headers["content-type"].startswith("application/vnd.openxmlformats-officedocument")
    copywriter_pdf = client.get(f"/api/export/copywriter/pdf/{generation_id}")
    assert copywriter_pdf.status_code == 200
    assert copywriter_pdf.headers["content-type"].startswith("application/pdf")
    designer_docx = client.get(f"/api/export/designer/docx/{generation_id}")
    assert designer_docx.status_code == 200
    assert designer_docx.headers["content-type"].startswith("application/vnd.openxmlformats-officedocument")
    designer_pdf = client.get(f"/api/export/designer/pdf/{generation_id}")
    assert designer_pdf.status_code == 200
    assert designer_pdf.headers["content-type"].startswith("application/pdf")
    assert client.delete(f"/api/generations/{generation_id}").status_code == 204
    assert client.get(f"/api/generations/{generation_id}").status_code == 404
