using HambotV2.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestPermissions
    {
        [TestMethod]
        public void Check_Allowed_Permission_Equal()
        {
            var permissionsService = new PermissionsService();

            Assert.IsTrue(permissionsService.CheckPermissions(0, 0));
        }

        [TestMethod]
        public void Check_Allowed_Permission_Higher()
        {
            var permissionsService = new PermissionsService();

            Assert.IsTrue(permissionsService.CheckPermissions(1, 0));
        }

        [TestMethod]
        public void Check_Disallowed_Permission_Lower()
        {
            var permissionsService = new PermissionsService();

            Assert.IsFalse(permissionsService.CheckPermissions(0, 1));
        }
    }
}
