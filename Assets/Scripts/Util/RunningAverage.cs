using System;
using System.Collections.Generic;
using UnityEngine;

// like a queue but is just implemented as a flat array, push() pop() like queue, but a push when the size is at it's max capacity will pop() automatically
//  push() add the items to the "head" of the collection which is accessed via [0] (so everything shifts by one index in the process, but the implementation does not copy anything)
//  pop() removes the items from the "tail" of the collection which is accessed via [size() - 1]
class CircularBuffer<T> {
	T[] arr = null;
	int cap = 0;
	int head = 0; // next item index to be written
	int cnt = 0;

	public CircularBuffer () {}
	public CircularBuffer (int capacity) {
		Resize(capacity);
	}

	public IReadOnlyList<T> Data () {
		return arr;
	}
	public int Capacity => cap;
	public int Count => cnt;

	public void Resize (int new_capacity) {
		var old_arr  = arr;
		var old_cap  = cap;
		var old_head = head;
		var old_cnt  = cnt;

		cap = new_capacity;
		arr = cap > 0 ? new T[cap] : null;

		cnt = cap <= old_cnt ? cap : old_cnt;
		for (int i=0; i<cnt; ++i) {
			arr[i] = old_arr[(old_head -cnt + old_cap + i) % old_cap];
		}

		head = cnt % cap;
	}

	public void PushOrOverwrite (T item) {
		// write in next free slot or overwrite if count == cap
		arr[head++] = item;
		head %= cap;

		if (cnt < cap)
			cnt++;
	}

	public T Pop () {
		int tail = head + (cap - cnt);
		tail %= cap;

		cnt--;
		return arr[tail];
	}
	public void PopN (int count) {
		cnt -= count;
	}

	// get ith oldest value
	public ref T GetOldest (int index) {
		int i = head + (cap - cnt);
		i += index;
		i %= cap;
		return ref arr[i];
	}

	// get ith newest value
	public ref T GetNewest (int index) {
		int i = head + cap - 1;
		i -= index;
		i = (i + cap) % cap;
		return ref arr[i];
	}

	public T this[int index] {
		get => GetNewest(index);
		set => GetNewest(index) = value;
	}
}

public class RunningAverage {
	CircularBuffer<float> buf;

	public RunningAverage (int capacity) {
		Resize(capacity);
	}

	public void Resize (int capacity) {
		buf = new CircularBuffer<float>(capacity);
	}

	public void Push (float val) {
		buf.PushOrOverwrite(val);
	}
	
	public struct Result { public float mean, min, max, std_dev; }

	public Result CalcTotalAvg () {
		if (buf.Count == 0)
			return new Result { mean=0, min=0, max=0, std_dev=0 };

		float total = 0;

		for (int i=0; i<buf.Count; ++i) {
			total += buf.GetOldest(i);
		}

		float mean = total / (float)buf.Count;

		float min = float.PositiveInfinity;
		float max = float.NegativeInfinity;
		float variance = 0;
		
		for (int i=0; i<buf.Count; ++i) {
			float val = buf.GetOldest(i);

			min = Mathf.Min(min, val);
			max = Mathf.Max(max, val);

			float tmp = val - mean;
			variance += tmp*tmp;
		}

		float std_dev = Mathf.Sqrt(variance / ((float)buf.Count - 1));
		
		return new Result { mean=mean, min=min, max=max, std_dev=std_dev };
	}
	public Result CalcTotalAvgPop () {
		var res = CalcTotalAvg();
		buf.PopN(buf.Count);
		return res;
	}
}

public class TimedAverage {
	RunningAverage running;
	float timer = 0;
	public RunningAverage.Result cur_result { get; private set; }
	
	public TimedAverage (int initial_count=256) {
		running = new RunningAverage(initial_count);
	}
	
	public void push (float val) {
		running.Push(val);
	}
	public void update (float update_period=1) {
		timer += Time.unscaledDeltaTime;

		if (timer > update_period) {
			cur_result = running.CalcTotalAvgPop();
			timer = 0;
		}
	}
}
