import sys
import os
import json

# Добавляем текущую папку в путь
sys.path.insert(0, os.path.dirname(__file__))

from openai import OpenAI
from prompts import build_prompt
from schemas import GenerationRequest

# Клиент для Ollama
client = OpenAI(
    base_url="http://localhost:11434/v1",
    api_key="ollama"
)

# Тестовые данные
test_data = {
    "theme": "Как малому бизнесу привлечь клиентов через Telegram",
    "audience": "Владельцы локальных сервисов и небольших интернет-магазинов",
    "gender": "Неважно",
    "age": "25-45",
    "pain": "Не понимаю, как привлечь клиентов без большого рекламного бюджета",
    "platform": "Telegram",
    "tone": "Экспертный",
    "format": "Карусель",
    "prompt": ""  # пользовательский промпт (опционально)
}

print("📤 Строим промпт...")

# СОЗДАЁМ ОБЪЕКТ PYDANTIC-МОДЕЛИ (ЭТО ВАЖНО!)
request_data = GenerationRequest(**test_data)
prompt = build_prompt(request_data)

print(f"📏 Длина промпта: {len(prompt)} символов")
print("\n--- ПЕРВЫЕ 500 СИМВОЛОВ ПРОМПТА ---")
print(prompt[:500] + "..." if len(prompt) > 500 else prompt)
print("\n---")

print("📤 Отправка запроса к модели llama3.2:3b...")

try:
    response = client.chat.completions.create(
        model="llama3.2:3b",
        messages=[
            {"role": "system", "content": "Ты — профессиональный SMM-продюсер. Отвечай только JSON-объектом. Никакого текста до или после."},
            {"role": "user", "content": prompt}
        ],
        temperature=0.8,
        max_tokens=4000
    )
    
    raw = response.choices[0].message.content
    print(f"✅ Ответ получен, длина: {len(raw)} символов")
    print("\n--- СЫРОЙ ОТВЕТ (первые 300 символов) ---")
    print(raw[:300] + "..." if len(raw) > 300 else raw)
    print("\n---")
    
    # Пробуем распарсить JSON
    try:
        data = json.loads(raw)
        print("✅ JSON успешно распарсен!")
        print(f"📋 Ключи: {list(data.keys())}")
        
        if "subtleties" in data:
            print("✅ Есть блок 'subtleties'")
        if "copywriter_brief" in data:
            print("✅ Есть блок 'copywriter_brief'")
        if "designer_brief" in data:
            print("✅ Есть блок 'designer_brief'")
            
    except json.JSONDecodeError as e:
        print(f"❌ Ошибка парсинга JSON: {e}")
        print("\n🔍 Ищем JSON в ответе...")
        
        import re
        match = re.search(r'\{.*\}', raw, re.DOTALL)
        if match:
            json_str = match.group(0)
            try:
                data = json.loads(json_str)
                print("✅ Найден JSON-объект, распарсен успешно!")
                print(f"📋 Ключи: {list(data.keys())}")
            except json.JSONDecodeError as e2:
                print(f"❌ Найденный JSON тоже невалиден: {e2}")
        else:
            print("❌ JSON-объект не найден в ответе")
            
except Exception as e:
    print(f"❌ Ошибка: {e}")
    import traceback
    traceback.print_exc()