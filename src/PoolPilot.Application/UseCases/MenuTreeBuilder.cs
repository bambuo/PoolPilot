using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Entities;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 把一组菜单装配成树。两个接口共用（我自己的树 / 管理视图），差别只在"传进来哪些菜单、
/// 每个节点的动作取什么"。
///
/// **父节点不在集合里时，子节点升为根**：按权限过滤之后，一个孩子有权而父亲无权是可能的，
/// 若此时把孤儿丢掉，使用者会看到"少了一个页面"却找不到原因。宁可让它暂时挂在根上。
/// </summary>
internal static class MenuTreeBuilder
{
    internal static IReadOnlyList<MenuNodeDto> Build(
        IReadOnlyList<Menu> menus,
        Func<Menu, IReadOnlyList<string>> actionsFor)
    {
        HashSet<long> present = [.. menus.Select(m => m.Id)];

        Dictionary<long, List<Menu>> childrenByParent = [];

        foreach (Menu menu in menus)
        {
            if (menu.ParentId is { } parent && present.Contains(parent))
            {
                if (!childrenByParent.TryGetValue(parent, out List<Menu>? kids))
                {
                    kids = [];
                    childrenByParent[parent] = kids;
                }

                kids.Add(menu);
            }
        }

        IEnumerable<Menu> roots = menus.Where(m => m.ParentId is not { } p || !present.Contains(p));

        return BuildLevel(roots, childrenByParent, actionsFor);
    }

    private static IReadOnlyList<MenuNodeDto> BuildLevel(
        IEnumerable<Menu> level,
        Dictionary<long, List<Menu>> childrenByParent,
        Func<Menu, IReadOnlyList<string>> actionsFor) =>
        [
            .. level
                .OrderBy(m => m.SortOrder)
                .ThenBy(m => m.Id)
                .Select(menu => new MenuNodeDto
                {
                    Id = menu.Id,
                    Code = menu.Code,
                    Title = menu.Title,
                    Path = menu.Path,
                    Icon = menu.Icon,
                    Kind = menu.Kind,
                    SortOrder = menu.SortOrder,
                    IsVisible = menu.IsVisible,
                    Actions = actionsFor(menu),
                    Children = childrenByParent.TryGetValue(menu.Id, out List<Menu>? kids)
                        ? BuildLevel(kids, childrenByParent, actionsFor)
                        : [],
                }),
        ];
}
