import httpx


OLLAMA_URL = "http://localhost:11434"
MODEL = "qwen3:14b"


def main() -> None:
    try:
        health_response = httpx.get(OLLAMA_URL, timeout=10)
        health_response.raise_for_status()
    except httpx.HTTPError as error:
        print(f"❌ Не удалось подключиться к Ollama по {OLLAMA_URL}: {error}")
        return

    try:
        response = httpx.post(
            f"{OLLAMA_URL}/api/chat",
            json={
                "model": MODEL,
                "messages": [{"role": "user", "content": "Привет! Напиши слово 'ОК' в ответ"}],
                "stream": False,
                "think": False,
            },
            timeout=120,
        )
        response.raise_for_status()
        data = response.json()
        text = data["message"]["content"].strip()
        if not text:
            raise RuntimeError("Ollama вернул ответ без текста")
        print(f"✅ Подключение к Ollama успешно! Ответ: {text}")
    except (httpx.HTTPError, KeyError, RuntimeError, ValueError) as error:
        print(f"❌ Ошибка при запросе к модели {MODEL}: {error}")


if __name__ == "__main__":
    main()
