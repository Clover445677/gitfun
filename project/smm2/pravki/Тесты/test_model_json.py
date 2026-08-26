from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:11434/v1",
    api_key="ollama"
)

prompt = """
Ты — SMM-специалист. Верни ТОЛЬКО JSON без пояснений:
{
  "status": "OK",
  "message": "Модель работает"
}
"""

try:
    response = client.chat.completions.create(
        model="llama3.2:3b",
        messages=[
            {"role": "system", "content": "Ты — помощник. Отвечай только JSON."},
            {"role": "user", "content": prompt}
        ],
        temperature=0.1,
        max_tokens=100
    )
    
    raw = response.choices[0].message.content
    print("Сырой ответ:")
    print(repr(raw))
    print("\n---")
    
    # Пробуем найти JSON
    import re
    import json
    
    # Удаляем Markdown
    clean = re.sub(r'```json\s*', '', raw)
    clean = re.sub(r'```\s*', '', clean)
    
    # Ищем JSON
    match = re.search(r'\{.*\}', clean, re.DOTALL)
    if match:
        json_str = match.group(0)
        data = json.loads(json_str)
        print("✅ JSON распарсен:", data)
    else:
        print("❌ JSON не найден")
        
except Exception as e:
    print("❌ Ошибка:", e)