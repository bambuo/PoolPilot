using System.Globalization;

using PoolPilot.Application.Abstractions;
using PoolPilot.Application.Dtos;
using PoolPilot.Domain.Constants;
using PoolPilot.Domain.Entities;
using PoolPilot.Domain.Repositories;

namespace PoolPilot.Application.UseCases;

/// <summary>
/// 「系统 → 用户管理」的账号列表。每个账号带它持有的角色——界面要据此显示角色标签，
/// 以及判断"这个账号还能不能删 superadmin"（唯一的超管要被挡住）。
///
/// 角色按 <c>roles</c> 表的固有顺序（主键升序，superadmin 在前）从左到右取，
/// 用户按 id 升序——同一份数据每次读出来的顺序必须一样，界面才有稳定的参照。
/// </summary>
public sealed class ListAdminUsersUseCase(
    IUserRepository users,
    IUserRoleRepository userRoles,
    IRoleRepository roles)
{
    public async Task<IReadOnlyList<AdminUserDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<User> allUsers = await users.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UserRoleBinding> bindings = await userRoles.ListAllAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Role> allRoles = await roles.ListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<long, HashSet<string>> codesByUser = [];
        foreach (UserRoleBinding binding in bindings)
        {
            if (!codesByUser.TryGetValue(binding.UserId, out HashSet<string>? codes))
            {
                codes = new HashSet<string>(StringComparer.Ordinal);
                codesByUser[binding.UserId] = codes;
            }

            codes.Add(binding.RoleCode);
        }

        return
        [
            .. allUsers.Select(user =>
            {
                HashSet<string> owned = codesByUser.GetValueOrDefault(user.Id, []);

                return new AdminUserDto
                {
                    Id = user.Id,
                    Email = user.Email.Value,
                    CreatedAt = user.CreatedAt.Utc.ToString(Iso8601UtcFormat, CultureInfo.InvariantCulture),
                    Roles = [.. allRoles.Where(r => owned.Contains(r.Code)).Select(ListRolesUseCase.ToDto)],
                };
            }),
        ];
    }

    /// <summary>ISO 8601 的 UTC 字面量（毫秒精度，末尾 Z）。</summary>
    private const string Iso8601UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
}

/// <summary>
/// 整体替换某个用户的角色绑定（提交什么就是什么）。
///
/// 三条守卫，按顺序：
/// <list type="number">
///   <item>用户必须存在（不存在回 400，与菜单管理那边"菜单 {id} 不存在"同一档）。</item>
///   <item>角色 code 必须全部存在，消息里点名是哪一个。</item>
///   <item>**不能把库里最后一个 superadmin 用户的超管角色摘掉**——判据是"库里还有没有
///     别的 superadmin 用户"，而不是"能不能改自己"：两个超管时互相摘对方都放行，
///     只剩一个时连它自己也不能摘（否则系统失去唯一的管理员，再也没人能进管理页）。</item>
/// </list>
/// </summary>
public sealed class SetUserRolesUseCase(
    IUserRepository users,
    IRoleRepository roles,
    IUserRoleRepository userRoles,
    IPolicyWriter policies)
{
    public async Task ExecuteAsync(long userId, UserRolesDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await users.GetAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ArgumentException($"用户 {userId} 不存在", nameof(userId));
        }

        List<string> codes =
        [
            .. request.RoleCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.Ordinal),
        ];

        IReadOnlyList<Role> known = await roles.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> knownCodes = [.. known.Select(r => r.Code)];

        foreach (string code in codes)
        {
            if (!knownCodes.Contains(code))
            {
                throw new ArgumentException($"角色「{code}」不存在", nameof(request));
            }
        }

        await EnsureNotRemovingLastSuperadminAsync(userId, codes, cancellationToken).ConfigureAwait(false);

        await userRoles.ReplaceAsync(userId, codes, cancellationToken).ConfigureAwait(false);
        await policies.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNotRemovingLastSuperadminAsync(
        long userId,
        IReadOnlyList<string> newCodes,
        CancellationToken cancellationToken)
    {
        if (newCodes.Contains(PermissionSubjects.SuperadminRoleCode))
        {
            return;
        }

        IReadOnlyList<string> current = await userRoles
            .ListRoleCodesForUserAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        if (!current.Contains(PermissionSubjects.SuperadminRoleCode))
        {
            return;
        }

        IReadOnlyList<UserRoleBinding> all = await userRoles.ListAllAsync(cancellationToken).ConfigureAwait(false);

        bool anotherSuperadminExists = all.Any(binding =>
            binding.UserId != userId && binding.RoleCode == PermissionSubjects.SuperadminRoleCode);

        if (!anotherSuperadminExists)
        {
            throw new ArgumentException(
                $"用户 {userId} 是库里最后一个持有 superadmin 的账号，不能移除它的超管角色，否则系统将失去唯一的管理员",
                nameof(userId));
        }
    }
}
