<Query Kind="Program">
  <NuGetReference>QuineMcCluskey</NuGetReference>
  <Namespace>QuineMcCluskey</Namespace>
  <Namespace>System.Diagnostics.CodeAnalysis</Namespace>
</Query>

void Main()
{
	var dataPath = Path.Combine(Util.CurrentQueryPath, "..\\pequod sample data.csv");
	var data = File.ReadAllLines(dataPath).Select(f => f.Split(",").ToArray()).ToArray();

	data.Dump("raw data", depth: 0);
	// data[((1 * 234) + (0 * 18) + 0) + 1].Dump();
	// data[((1 * 234) + (1 * 18) + 1) + 1][3] = "FALSE";
	var fingerprints = GenerateFingerprints(data).Dump("fingerprints", depth: 0);
	var groups = GenerateGroups(fingerprints).Dump("groups", depth: 0);

	$"Estimated grouping efficiency: {((float)(fingerprints.Sum(f => f.Value.Count) - groups.Sum(g => g.Value.Groups.Length)) / ((float)fingerprints.Sum(f => f.Value.Count))) * 100}%".Dump();

	var variables = GenerateVariables(groups).Dump("variables", depth: 0);
	
	var reduced = GenerateReducedMatrix(groups);
	reduced.ToDictionary().Dump("reduced", depth: 0);

	$"Matrix reduction efficiency: {((float)(data.Length - reduced.Lengths.Aggregate((a, p) => a * p)) / ((float)data.Length)) * 100}%".Dump();

	var terms = GenerateTruthTableTerms(variables, reduced).Dump("terms", depth: 0);
	
	$"Estimated QMC search efficiency: {((float)((1 << variables.Length) - terms.DCTerms.Length) / ((float)(1 << variables.Length))) * 100}%".Dump();

	var implicants = QuineMcCluskeySolver.QMC_Solve(terms.MaxTerms, terms.DCTerms);
	implicants
		.Select(imp => new { imp = imp.MinTerms.Select(mt => new { mt, bin = Convert.ToString(mt, 2) }), cb = imp.ToString().Replace('X', '-') })
		.Dump("implicants", depth: 0);

	var impValMasks = implicants.Select(imp =>
	{
		// this is 100% terrible
		var bits = Convert.ToInt32(imp.ToString().Replace('X', '0'), 2);
		var mask = Convert.ToInt32(imp.ToString().Replace('0', '1').Replace('X', '0'), 2);
		return new ValMask(bits, mask, variables.Length);
	});
	
	var declRules = GenerateDeclarativeRules(impValMasks, variables);
	declRules.Dump("incompatible choices");
}

// ===================================================== STEP 1: FINGERPRINTING
// For each choice, we scan across the all the configurations with that choice,
// and construct a 'fingerprint' indicating whether each configuration is valid
// or not. This FP is later used to group similar choices together (step 2) and
// to construct a reduced matrix (step 3).
//
// The fingerprint is essentially just concatenating the IS_VALID column as
// bits into a arbitrarily long bit array. Assuming the input matrix is
// complete and properly ordered, this process is extremely predictable and
// stable. Further, it's much more efficient to compare two choices by their
// fingerprints than by comparing all configurations associated with them.
Dictionary<string, Dictionary<string, Fingerprint>> GenerateFingerprints(string[][] data)
{
	var hashes = new Dictionary<string, Dictionary<string, Fingerprint>>();
	var choiceCounts = new Dictionary<string, int>();
	var options = data[0];
	var ymax = options.Length - 1;
	
	for (var x = 1; x < data.Length; x++)
	{
		for (var y = 0; y < ymax; y++)
		{
			var optionName = options[y];
			var choiceName = data[x][y];
			var isValid = bool.Parse(data[x][ymax]);
			var optionHashes = hashes.GetValueOrDefault(optionName);
			if (optionHashes == null) {
				optionHashes = hashes[optionName] = new Dictionary<string, Fingerprint>();
			}
			
			var hash = optionHashes.GetValueOrDefault(choiceName);
			if (hash == null) {
				hash = new Fingerprint();
				optionHashes[choiceName] = hash;
			}
			
			hash.Append(isValid);
		}
	}

	return hashes;
}

class Fingerprint : IEquatable<Fingerprint>
{
	// TODO: Convert to struct + builder

	private List<byte> bytes = new List<byte>();

	public int Length { get; private set; }

	public byte[] ToArray() => this.bytes.ToArray();

	public bool GetBit(int bit) => (this.bytes[bit / 8] & (byte)(1 << (bit % 8))) != 0;

	public int Append(bool bit)
	{
		if (Length % 8 == 0)
		{
			bytes.Add(0);
		}

		if (bit)
		{
			bytes[Length / 8] |= (byte)(1 << (Length % 8));
		}

		return ++Length;
	}

	public override string ToString()
	{
		return this.Length + ":" + Convert.ToBase64String(this.bytes.ToArray());
	}

	public override bool Equals(object obj)
	{
		return obj is Fingerprint && this.Equals(obj);
	}

	public bool Equals(Fingerprint other)
	{
		if (other == null)
		{
			return false;
		}

		if (Object.ReferenceEquals(this, other))
		{
			return true;
		}

		return (this.Length == other.Length) && Enumerable.SequenceEqual(this.bytes, other.bytes);
	}

	public override int GetHashCode()
	{
		var result = this.Length * 15013;
		foreach (var b in this.bytes)
		{
			result = result * 31 + b;
		}

		return result;
	}

	public static bool operator ==(Fingerprint x, Fingerprint y)
	{
		return Object.ReferenceEquals(x, null) ? Object.ReferenceEquals(y, null) : x.Equals(y);
	}

	public static bool operator !=(Fingerprint x, Fingerprint y)
	{
		return !(x == y);
	}
}


// ===================================================== STEP 2: CLASSIFICATION
// Group together all choices in each group that have an identical fingerprint.
// Two choices with the same fingerprint may be treated interchangeably by
// rules, that is, if A and B have the same fingerprint, then every valid
// configuration with A corresponds to a valid configuration with B, and vice-
// versa. By grouping them together early, we reduce the amount of work needed
// by the QMC algorithm later (step 5).
//
// Simultaneously, we compute the 'sprawl' for each option. The sprawl is
// equivalent to the number of continuous rows in a full, properly-ordered
// matrix over which only a single choice value for that option is present,
// e.g. a sprawl of 128 means if a given choice first appears on row n, the
// next choice for this option will first appear on row n + 128. Sprawl is also
// used to consturct a reduced matrix (step 3).
Dictionary<string, ChoiceGroupCollection> GenerateGroups(Dictionary<string, Dictionary<string, Fingerprint>> fingerprints)
{
	var groups = fingerprints.Select(o => (
			key: o.Key,
			value: new ChoiceGroupCollection
			{
				// Ugly linq f*ckery. Can probably be improved
				Groups = o.Value
					.Select((fp, i) => (fp, index: i))
					.GroupBy(pair => pair.fp.Value)
					.Select((g, i) => new ChoiceGroup
					{
						Choices = g.Select(c => c.fp.Key).ToArray(),
						Fingerprint = g.Key,
						Sample = g.First().index,
						Index = i,
					})
					.ToArray()
			}))
			.ToList();

	// Compute 'sprawl'
	groups[groups.Count - 1].value.Sprawl = 1;
	for (int i = groups.Count - 2, prod = 1; i >= 0; i--)
	{
		groups[i].value.Sprawl = prod *= fingerprints[groups[i + 1].key].Count;
	}

	return groups.ToDictionary(g => g.key, g => g.value);
}

class ChoiceGroup
{
	public string[] Choices { get; set; }
	public Fingerprint Fingerprint { get; set; }
	public int Sample { get; set; }
	public int Index { get; set; }
}

class ChoiceGroupCollection
{
	public ChoiceGroup[] Groups { get; set; }
	public int Sprawl { get; set; }
}


// ========================================================== STEP 3: REDUCTION
// Convert the full matrix into a reduced matrix. Whereas the full matrix shows
// every possible configuration, with each row identified by a set of choices,
// the reduced matrix shows every logically distinct group of configurations
// with each row identified by a set of choice groups. Due to the way
// classification is done (step 2), it's guaranteed that the configurations in
// each row of the reduced matrix -- that is, the configurations consisting of
// one of each choice for each choice group identified in the row -- shall be
// either all valid or all invalid.
//
// Construction of this reduced matrix is done by 'deconstructing' the
// fingerprints (step 1) of the choices in the first option. The process may be
// a little confusing, but it's essentially just a matter of computing the
// position in the full matrix of the first configuration in the group of
// configurations represented in the reduced matrix.
ReducedMatrix GenerateReducedMatrix(Dictionary<string, ChoiceGroupCollection> groups)
{
	var list = groups.ToList();
	var first = list[0].Value;
	var lengths = list.Select(g => g.Value.Groups.Length).ToArray();

	var max = lengths.Aggregate((a, x) => a * x);
	var matrix = new ReducedMatrix(lengths);
	for (var x = 0; x < max; x++)
	{
		var indices = new int[lengths.Length];
		var indexSerial = x;
		var fpIndex = 0;

		indices[0] = indexSerial % lengths[0];
		indexSerial /= lengths[0];
		
		for (var i = 1; i < list.Count; i++)
		{
			indices[i] = indexSerial % lengths[i];
			indexSerial /= lengths[i];
			fpIndex += list[i].Value.Groups[indices[i]].Sample * list[i].Value.Sprawl;
		}

		matrix.Set(first.Groups[indices[0]].Fingerprint.GetBit(fpIndex), indices); 
	}

	return matrix;
}


// Internally, the reduced matrix is a variadic array, though for troubleshooting 
// we can think of it as a dictionary.
class ReducedMatrix
{
	private Array data;
	private int[] lengths;

	public IReadOnlyCollection<int> Lengths => Array.AsReadOnly(this.lengths);
	public int Dimensions => this.lengths.Length;

	public ReducedMatrix(int[] lengths)
	{
		this.lengths = lengths;
		this.data = Array.CreateInstance(typeof(bool), lengths);
	}

	public bool Get(params int[] indices)
	{
		return (bool)data.GetValue(indices);
	}

	public void Set(bool value, params int[] indices)
	{
		data.SetValue(value, indices);
	}

	public Dictionary<string, bool> ToDictionary()
	{
		var max = lengths.Aggregate((a, x) => a * x);
		var dict = new Dictionary<string, Boolean>();
		for (var x = 0; x < max; x++)
		{
			var indices = MixedRadix.ToMixedRadixForm(x, lengths);
			dict.Add(string.Join(' ', indices), (bool)data.GetValue(indices));
		}

		return dict;
	}
}

// Utilities for swapping back and from from mixed radix forms. We don't
// _actually_ use these internally for performance reasons, but they're useful
// for troubleshooting and to demonstrate the theory.
static class MixedRadix
{
	public static int[] ToMixedRadixForm(int n, int[] radices)
	{
		var result = new int[radices.Length];
		for (var i = 0; i < radices.Length; i++)
		{
			result[i] = n % radices[i];
			n /= radices[i];
		}

		return result;
	}

	public static int FromMixedRadixForm(int[] indices, int[] radices)
	{
		if (indices.Length > radices.Length)
		{
			throw new IndexOutOfRangeException("index array may not be longer than radix array");
		}

		if (indices.Length < radices.Length)
		{
			Array.Resize(ref radices, indices.Length);
		}

		var sprawl = new int[radices.Length];
		sprawl[sprawl.Length - 1] = 1;
		for (int i = sprawl.Length - 2, prod = 1; i >= 0; i--)
		{
			sprawl[i] = prod *= radices[i];
		}

		var result = 0;
		for (var i = 0; i < radices.Length; i++)
		{
			result += indices[i] * sprawl[i];
		}

		return result;
	}
}


VariableSet GenerateVariables(Dictionary<string, ChoiceGroupCollection> groups)
{
	int bit = 1;
	int length = groups.Sum(g => g.Value.Groups.Count());
	var optionVars = groups.Select(
		g => {
			int mask = 0;
			var vars = new List<(int bit, ChoiceGroup group)>(g.Value.Groups.Length);
			foreach(var gr in g.Value.Groups)
			{
				mask |= bit;
				vars.Add((bit, gr));
				bit <<=1;
			}

			return new OptionVariableSet
			{
				Mask = mask,
				OptionName = g.Key,
				Variables = vars.Select(v => new Variable
				{
					ValMask = new ValMask(v.bit, mask, length),
					Groups = new[] { v.group }
				}).ToDictionary(v => v.ValMask.Bits),
			};
		}
	).ToArray();
	
	return new VariableSet
	{
		Length = length,
		OptionVariables = optionVars,
	};
}

struct ValMask : IEquatable<ValMask>
{
	public int Bits { get; private set; }
	public int Mask { get; private set; }
	public int Length { get; private set; }

	public ValMask(int bits, int mask, int length)
	{
		Bits = bits & mask;
		Mask = mask;
		Length = length;
	}

	public bool Equals(ValMask other)
	{
		return Length == other.Length && Mask == other.Mask && Bits == other.Bits;
	}

	public override bool Equals(object obj)
	{
		return obj is ValMask && Equals((ValMask)obj);
	}

	public override int GetHashCode()
	{
		return 139793779 ^ Bits ^ Mask ^ (1 << Length);
	}

	public override string ToString()
	{
		var chars = new char[Length];
		for (int c = 0, b = (1 << Length - 1); b > 0; b >>= 1, c++)
		{
			chars[c] = (b & Mask) == 0 ? '-' : (b & Bits) == 0 ? '0' : '1';
		}

		return new string(chars);
	}
}

class Variable
{
	public ValMask ValMask { get; set; }
	public ChoiceGroup[] Groups { get; set; }
}

class OptionVariableSet
{
	public int Mask { get; set; }
	public string OptionName { get; set; }
	public Dictionary<int, Variable> Variables { get; set; }
}

class VariableSet
{
	public int Length { get; set; }
	public OptionVariableSet[] OptionVariables { get; set; }
}



TermSet GenerateTruthTableTerms(VariableSet variables, ReducedMatrix matrix)
{
	var limit = 1 << variables.Length;
	var max = new List<int>(variables.Length);
	var min = new List<int>(variables.Length);
	var dcs = new List<int>(limit / 2);

	var dim = matrix.Dimensions;
	var indices = new int[dim];
	// TODO: this can definitely be improved for performance
	for (int term = 0; term < limit; term++)
	{
		bool found = true;
		for (int d = 0; d < dim; d++)
		{
			var ov = variables.OptionVariables[d];
			found = ov.Variables.TryGetValue(term & ov.Mask, out var variable);
			if (found)
			{
				// TODO: currently only works with naive variable generation algorithm
				indices[d] = variable.Groups[0].Index;
			}
			else
			{
				dcs.Add(term);
				break;
			}
		}
		
		if (found)
		{
			(matrix.Get(indices) ? min : max).Add(term);
		}
	}

	return new TermSet
	{
		MinTerms = min.ToArray(),
		MaxTerms = max.ToArray(),
		DCTerms = dcs.ToArray(),
	};
}

class TermSet
{
	public int[] MinTerms { get; set; }
	public int[] MaxTerms { get; set; }
	public int[] DCTerms { get; set; }
}


IEnumerable<Dictionary<string, string[]>> GenerateDeclarativeRules(IEnumerable<ValMask> implicants, VariableSet variables)
{
	foreach (var imp in implicants)
	{
		Dictionary<string, string[]> dict = new Dictionary<string, string[]>();
		foreach (var ov in variables.OptionVariables)
		{
			if ((imp.Mask & ov.Mask) == 0)
			{
				continue;
			}
			var variable = ov.Variables[imp.Bits & ov.Mask];
			dict.Add(ov.OptionName, variable.Groups.Select(g => g.Choices.AsEnumerable()).Aggregate((a, c) => a.Intersect(c)).ToArray());
		}
		
		yield return dict;
	}
}