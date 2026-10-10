// https://github.com/stryker-mutator/stryker-net/issues/340

using System.Collections.Generic;
using System.Linq;

namespace TargetProject.Defects
{
    public class Linq
    {
        public void OrderByExample()
        {
            var someList = new List<int> { 1, 2, 3, 4, 5 };
            someList.OrderBy(i => i.ToString());
        }

        public static int[] FilterPositive(IEnumerable<int> values) =>
            (from value in values where value > 0 select value).ToArray();

        public static int[] OrderAscending(IEnumerable<int> values) =>
            (from value in values orderby value select value).ToArray();

        public static int[] OrderDescending(IEnumerable<int> values) =>
            (from value in values orderby value descending select value).ToArray();

        public static int[] OrderByGroupAndValue(IEnumerable<(int Group, int Value)> values) =>
            (from item in values orderby item.Group, item.Value select item.Value).ToArray();
    }
}
