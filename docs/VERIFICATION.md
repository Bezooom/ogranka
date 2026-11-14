# Проверка стенда

Дата прогона: 2026-11-14. Машина: Linux, x64. SDK: .NET 10.0.115. Рантайм на стенде: только `Microsoft.NETCore.App 10.0.15`. Таргет `net48` собран через compile-time пакет `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3. Tekla Structures на стенде не установлена.

Команда:

```bash
dotnet build -c Release
dotnet run -c Release -f net10.0 -- --self-test
dotnet build -c Release -f net48
```

`dotnet build -c Release` собирает оба таргета. `--self-test` гоняет гейты и пишет `test_output.stp`. Образец того же файла лежит в [`samples/i-beam-web-hole.stp`](../samples/i-beam-web-hole.stp).

## Гейты

| Требование | Вердикт | Факт |
| --- | --- | --- |
| Обратная матрица восстанавливает точку | PASS | ошибка < 1e-9 |
| Куб 10 мм | PASS | объём 1000 мм³ |
| Клиппинг куба плоскостью z=7 | PASS | объём 700 мм³ |
| Прямоугольный проём 4×4 сквозь куб 10 мм | PASS | объём 840 мм³, STEP замкнут |
| IFC `IfcRelVoidsElement`, тот же проём | PASS | объём 840 мм³ |
| Двутавр 300×200×100, стенка 8, полка 12, отверстие 40×60 | PASS | объём 1 123 200 мм³ |
| STEP-граф образца | PASS | `AUTOMOTIVE_DESIGN`, мм, нет висячих ссылок, каждое ребро в двух противоположных `ORIENTED_EDGE` |
| Грани и вершины образца | PASS | 18 `ADVANCED_FACE`, 32 `VERTEX_POINT`, есть `FACE_BOUND` |
| Писатель не открыл оболочку | PASS | 0 предупреждений |
| IFC-куб, сдвиг X=100 | PASS | объём 1000 мм³, min X = 100 |
| IFC-выдавливание 10×20×30 | PASS | объём 6000 мм³ |
| IFC-двутавр 100×200×8×12, длина 300 | PASS | объём 1 142 400 мм³ |
| IFC-клиппинг z>7 | PASS | объём 700 мм³ |
| IFC в метрах, куб 1 м | PASS | 1e9 мм³ |
| IFC `IfcMappedItem`, сдвиг 50 мм | PASS | min X = 45, объём 1000 мм³ |
| Сборка `net48` x64 | PASS | `bin/Release/net48/TeklaToStep.exe` |
| `--live` без Tekla | PASS как отказ | код 2, текст «Tekla Open API есть только в Windows» |

Это проверка собственного графа и синтетических IFC. Это не импорт в CAD и не сеанс Tekla.

## Что в образце

`samples/i-beam-web-hole.stp`:

- схема `AUTOMOTIVE_DESIGN`;
- единицы `( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )`;
- `MANIFOLD_SOLID_BREP('I-beam-web-hole', #closed_shell)`;
- `ADVANCED_BREP_SHAPE_REPRESENTATION`, первый item — `AXIS2_PLACEMENT_3D`;
- отверстие в стенке записано как `FACE_BOUND`, не как отдельное тело.

## Чего этот прогон не доказывает

- Файл не открывался в FreeCAD, SolidWorks, Inventor или Rhino.
- Не было файла IFC, выгруженного Tekla. Фикстуры написаны вручную под IFC2x3.
- Ветка `#if TEKLA` не компилировалась: на стенде нет `Tekla.Structures.Model.dll`.
- `--live` не вызывал `GetSolid()` у выбранной балки, пластины, полибалки или сборки.
- Скругления профиля в гейтах не участвовали. Писатель их срезает в острую кромку и пишет предупреждение.
