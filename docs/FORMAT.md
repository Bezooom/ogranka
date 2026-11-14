# Почему не чистый FACETED_BREP

Запрос был на ASCII-грань с полной рёберной цепочкой: точка, вершина, направление, вектор, линия, `EDGE_CURVE`, `ORIENTED_EDGE`, `EDGE_LOOP`, плоскость, грань, оболочка, тело.

Чистый `FACETED_BREP` по ISO 10303-42 этот список не принимает. У него грани `FACE_SURFACE`, а границы только `POLY_LOOP`. `EDGE_CURVE` в такой оболочке — нарушение WHERE-правила. SolidWorks читает такой файл нестабильно.

Писатель отдаёт форму, которую уже открывают и FreeCAD, и SolidWorks:

- `FILE_SCHEMA(('AUTOMOTIVE_DESIGN'))`, как в файлах SolidWorks и в OCC `cube.stp`;
- цепочка продукта: `APPLICATION_PROTOCOL_DEFINITION` → `MECHANICAL_CONTEXT` → `PRODUCT` → `ADVANCED_BREP_SHAPE_REPRESENTATION`;
- первый item представления — `AXIS2_PLACEMENT_3D`;
- тело — `MANIFOLD_SOLID_BREP` (родитель `FACETED_BREP`) с `CLOSED_SHELL` и `ADVANCED_FACE` / `PLANE`;
- единицы — `SI_UNIT(.MILLI.,.METRE.)`.

`MANIFOLD_SOLID_BREP` здесь не «другой формат вместо граней». Грани плоские, рёбра прямые, общие вершины склеены с допуском 1e-4 мм. Отличие только в том, что граница грани — рёберная петля, а не `POLY_LOOP`.

Ориентир по заголовку и цепочке продукта: [OCC cube.stp](https://github.com/jeromerobert/jCAE/blob/master/occjava/test/input/cube.stp). Этот репозиторий не проверял, что наш файл открывается тем же импортером. Совпадение заголовка — решение по опубликованному примеру, не результат прогона FreeCAD.

Незамкнутая сетка не притворяется телом: пишется `OPEN_SHELL` и предупреждение. Внутренняя полость, не сквозное отверстие, пишется как `BREP_WITH_VOIDS`. Сквозное отверстие остаётся одной оболочкой с `FACE_BOUND`.
