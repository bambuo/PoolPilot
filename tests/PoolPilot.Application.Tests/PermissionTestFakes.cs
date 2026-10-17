using PoolPilot.Application.Abstractions;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;
using PoolPilot.Domain.ValueObjects;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 授权这一组用例共用的假实现：菜单 / 角色 / 两张绑定表 / 判定器 / 写侧 / 当前用户。
///
/// 放在单独一个文件：它们必须**行为一致**地服务多个测试（树过滤、菜单 CRUD、角色绑定、
/// 注册首账号），在两个文件里各写一份就会开始各测各的。
/// </summary>
internal sealed class FakeMenuRepository : IMenuRepository
{
    private readonly List<Menu> _menus = [];
    private long _nextId = 1;

    public IReadOnlyList<Menu> Menus => _menus;

    public Task<IReadOnlyList<Menu>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Menu>>([.. _menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id)]);

    public Task<Menu?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_menus.FirstOrDefault(m => m.Id == id));

    public Task<Menu?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_menus.FirstOrDefault(m => m.Code == code));

    public Task<bool> HasChildrenAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_menus.Any(m => m.ParentId == id));

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_menus.Count);

    public Task<Menu> CreateAsync(Menu menu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        Menu stored = menu with { Id = _nextId++ };
        _menus.Add(stored);

        return Task.FromResult(stored);
    }

    public Task UpdateAsync(Menu menu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menu);

        int index = _menus.FindIndex(m => m.Id == menu.Id);

        if (index < 0)
        {
            throw new ArgumentException($"菜单 {menu.Id} 不存在", nameof(menu));
        }

        _menus[index] = menu;

        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        _menus.RemoveAll(m => m.Id == id);

        return Task.CompletedTask;
    }

    /// <summary>直接摆一条菜单（省去走一遍用例）。</summary>
    public Menu Seed(
        string code,
        string kind = MenuKinds.Menu,
        long? parentId = null,
        IReadOnlyList<string>? actions = null,
        int sortOrder = 0,
        bool isVisible = true,
        string? path = null)
    {
        Menu menu = new()
        {
            Id = _nextId++,
            ParentId = parentId,
            Code = code,
            Title = code,
            Path = path ?? (kind == MenuKinds.Group ? null : $"/{code}"),
            Icon = null,
            Kind = kind,
            SortOrder = sortOrder,
            IsVisible = isVisible,
            PermissionKey = PermissionSubjects.ForMenu(code),
            Actions = actions ?? [MenuActions.View],
            CreatedAt = LighterTimestamp.FromMilliseconds(0),
        };

        _menus.Add(menu);

        return menu;
    }
}

internal sealed class FakeRoleRepository : IRoleRepository
{
    private readonly List<Role> _roles = [];
    private long _nextId = 1;

    public IReadOnlyList<Role> Roles => _roles;

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Role>>(_roles);

    public Task<Role?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles.FirstOrDefault(r => r.Code == code));

    public Task<Role?> FindRegistrationDefaultAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_roles.FirstOrDefault(r => r.IsRegistrationDefault));

    public Task<Role> CreateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        // 与真实仓储同一条约束：默认角色全库最多一个
        if (role.IsRegistrationDefault)
        {
            ClearDefault();
        }

        Role stored = role with { Id = _nextId++ };
        _roles.Add(stored);

        return Task.FromResult(stored);
    }

    public Task UpdateAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        int index = _roles.FindIndex(r => r.Id == role.Id);

        if (index < 0)
        {
            throw new ArgumentException($"角色 {role.Id} 不存在", nameof(role));
        }

        _roles[index] = role;

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string code, CancellationToken cancellationToken = default)
    {
        _roles.RemoveAll(r => r.Code == code);

        return Task.CompletedTask;
    }

    public Task SetRegistrationDefaultAsync(string? code, CancellationToken cancellationToken = default)
    {
        if (code is not null && !_roles.Any(r => r.Code == code))
        {
            throw new ArgumentException($"角色「{code}」不存在，无法设为注册默认", nameof(code));
        }

        for (int i = 0; i < _roles.Count; i++)
        {
            _roles[i] = _roles[i] with { IsRegistrationDefault = code is not null && _roles[i].Code == code };
        }

        return Task.CompletedTask;
    }

    public Role Seed(string code, string title = "", bool isSystem = false, bool isRegistrationDefault = false)
    {
        if (isRegistrationDefault)
        {
            ClearDefault();
        }

        Role role = new()
        {
            Id = _nextId++,
            Code = code,
            Title = title,
            IsSystem = isSystem,
            IsRegistrationDefault = isRegistrationDefault,
        };

        _roles.Add(role);

        return role;
    }

    private void ClearDefault()
    {
        for (int i = 0; i < _roles.Count; i++)
        {
            _roles[i] = _roles[i] with { IsRegistrationDefault = false };
        }
    }
}

internal sealed class FakeUserRoleRepository : IUserRoleRepository
{
    private readonly List<UserRoleBinding> _bindings = [];

    public IReadOnlyList<UserRoleBinding> Bindings => _bindings;

    public Task<IReadOnlyList<string>> ListRoleCodesForUserAsync(
        long userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. _bindings.Where(b => b.UserId == userId).Select(b => b.RoleCode)]);

    public Task<int> CountForUserAsync(long userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bindings.Count(b => b.UserId == userId));

    public Task AddAsync(long userId, string roleCode, CancellationToken cancellationToken = default)
    {
        if (!_bindings.Any(b => b.UserId == userId && b.RoleCode == roleCode))
        {
            _bindings.Add(new UserRoleBinding(userId, roleCode));
        }

        return Task.CompletedTask;
    }

    public Task ReplaceAsync(long userId, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        _bindings.RemoveAll(b => b.UserId == userId);
        _bindings.AddRange(roleCodes.Select(code => new UserRoleBinding(userId, code)));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserRoleBinding>>(_bindings);
}

internal sealed class FakeMenuRoleRepository : IMenuRoleRepository
{
    private readonly List<MenuRoleBinding> _bindings = [];

    public IReadOnlyList<MenuRoleBinding> Bindings => _bindings;

    public Task<IReadOnlyList<string>> ListRoleCodesAsync(long menuId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            [.. _bindings.Where(b => b.MenuId == menuId).Select(b => b.RoleCode).OrderBy(c => c, StringComparer.Ordinal)]);

    public Task ReplaceAsync(long menuId, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleCodes);

        _bindings.RemoveAll(b => b.MenuId == menuId);
        _bindings.AddRange(roleCodes.Select(code => new MenuRoleBinding(menuId, code)));

        return Task.CompletedTask;
    }

    public Task ReplaceForRoleAsync(
        string roleCode,
        IReadOnlyList<long> menuIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(menuIds);

        _bindings.RemoveAll(b => b.RoleCode == roleCode);
        _bindings.AddRange(menuIds.Select(id => new MenuRoleBinding(id, roleCode)));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MenuRoleBinding>> ListAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MenuRoleBinding>>(_bindings);
}

/// <summary>假判定器：授权集由测试直接摆。</summary>
internal sealed class FakePermissionEvaluator : IPermissionEvaluator
{
    private readonly List<PermissionGrant> _granted = [];

    public IReadOnlyList<PermissionGrant> Granted => _granted;

    public int GetGrantedCallCount { get; private set; }

    public bool Has(string permissionKey, string action) =>
        _granted.Any(g => g.PermissionKey == permissionKey && g.Actions.Contains(action));

    public Task<IReadOnlyList<PermissionGrant>> GetGrantedAsync(
        IReadOnlyList<PermissionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        GetGrantedCallCount++;

        // 与真实现同口径：**只在候选范围内回答**（真实现是逐候选问 matcher）。
        // 候选没列出的权限点/动作，真实现不会给出，这里也不给——否则用例测试会
        // 在"菜单表里没有这个权限点"时也看到授权，测出来的是假象。
        Dictionary<string, IReadOnlyList<string>> actionsByKey = new(StringComparer.Ordinal);
        foreach (PermissionCandidate candidate in candidates)
        {
            actionsByKey[candidate.PermissionKey] = candidate.Actions;
        }

        List<PermissionGrant> granted =
        [
            .. _granted
                .Where(g => actionsByKey.ContainsKey(g.PermissionKey))
                .Select(g => new PermissionGrant(
                    g.PermissionKey,
                    [.. g.Actions.Where(action => actionsByKey[g.PermissionKey].Contains(action))]))
                .Where(g => g.Actions.Count > 0),
        ];

        return Task.FromResult<IReadOnlyList<PermissionGrant>>(granted);
    }

    public void Grant(string permissionKey, params string[] actions) =>
        _granted.Add(new PermissionGrant(permissionKey, actions));
}

internal sealed class FakeCurrentUser : ICurrentUser
{
    public long? UserId { get; set; }
}

/// <summary>假写侧：只数被重建了几次（"每次写操作后重建"这句话的可断言形式）。</summary>
internal sealed class FakePolicyWriter : IPolicyWriter
{
    public int RebuildCount { get; private set; }

    public Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        RebuildCount++;

        return Task.CompletedTask;
    }
}
