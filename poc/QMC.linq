<Query Kind="Program">
  <NuGetReference>QuineMcCluskey</NuGetReference>
  <Namespace>QuineMcCluskey</Namespace>
  <Namespace>QuineMcCluskey.Enums</Namespace>
</Query>

int CommonBits(int[] set) {
	var mask = int.MaxValue;
	var max = set.Length - 1;
	for (int i = 1; i < max; i ++) {
		mask &= ~(set[i] ^ set[i + 1]);
	}
	return mask;
}

void Main()
{
	QuineMcCluskeySolver
		.QMC_Solve(
			new[] {
				38, 42, 70, 73, 74,
			},
			new[] {
				0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,23,24,27,28,29,30,31,32,33,34,35,36,39,40,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,71,72,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,123,124,125,126,127,
			})
		.Select(imp => new { imp = imp.MinTerms.Select(mt => new { mt, bin = Convert.ToString(mt, 2) }), cb = imp.ToString() })
		.Dump();
}

//.Select(imp => imp.MinTerms.Aggregate((b, t) => b &
