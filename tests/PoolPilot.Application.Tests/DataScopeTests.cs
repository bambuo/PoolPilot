using PoolPilot.Application.Services;
using PoolPilot.Domain.Constants;

namespace PoolPilot.Application.Tests;

/// <summary>
/// 用户数据作用域：权限之外的第二道边界。
///
/// 这里锁住两个不会从界面看出来的方向：普通账号不能碰无归属的老行
/// （NULL 不能被解释成"大家的"），以及 superadmin 的全量视图确实覆盖无归属行。
/// </summary>
[TestClass]
public sealed class DataScopeTests
{
    [TestMethod]
    public async Task NormalUser_OnlyOwnsTheirRows_AndCannotClaimOrphanRows()
    {
        FakeUserRoleRepository userRoles = new();
        await userRoles.AddAsync(7, "viewer");

        UserDataScope scope = new(new FakeCurrentUser { UserId = 7 }, userRoles);

        Assert.AreEqual(7L, scope.UserId);
        Assert.IsFalse(await scope.CanSeeAllAsync());
        Assert.AreEqual(7L, await scope.OwnerFilterAsync());

        Assert.IsTrue(await scope.CanAccessOwnerAsync(7), "自己的行可访问");
        Assert.IsFalse(await scope.CanAccessOwnerAsync(8), "别人的行不可访问");
        Assert.IsFalse(await scope.CanAccessOwnerAsync(null), "无归属的老行不能被普通账号认领");
    }

    [TestMethod]
    public async Task Superadmin_SeesAllOwnersIncludingOrphanRows()
    {
        FakeUserRoleRepository userRoles = new();
        await userRoles.AddAsync(1, PermissionSubjects.SuperadminRoleCode);

        UserDataScope scope = new(new FakeCurrentUser { UserId = 1 }, userRoles);

        Assert.IsTrue(await scope.CanSeeAllAsync());
        Assert.IsNull(await scope.OwnerFilterAsync(), "全量视图不按 owner 过滤");
        Assert.IsTrue(await scope.CanAccessOwnerAsync(8));
        Assert.IsTrue(await scope.CanAccessOwnerAsync(null), "无归属老行需要管理员能接手");
    }
}
